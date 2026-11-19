#include "film_renderer.h"
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <deque>
#include <limits>
#include <mutex>
#include <string>
#include <vector>
#include <d3d10.h>
#include <dxgi1_6.h>
#include <ass/ass.h>
#include <libplacebo/d3d11.h>
#include <libplacebo/renderer.h>
#define PL_LIBAV_IMPLEMENTATION 0
#include <libplacebo/utils/libav.h>
extern "C" {
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_d3d11va.h>
#include <libavutil/pixdesc.h>
}

#if PL_API_VER != 387 || !defined(PL_HAVE_LAV_DOLBY_VISION)
#error FilmRenderer requires libplacebo v7.387.0 with Dolby Vision support.
#endif

namespace {
template<class T> void release(T *&value) { if (value) value->Release(); value = nullptr; }
void ass_log(int, const char *, va_list, void *) { } // Never log subtitle contents.
constexpr size_t max_subtitle_bytes = 64 * 1024 * 1024;
constexpr size_t max_subtitle_rects = 256;
constexpr int max_text_events = 4096;
constexpr size_t max_text_bytes = 1024 * 1024;

bool display_window(HWND window)
{
    return window && IsWindow(window) && GetAncestor(window, GA_ROOT) == window;
}

bool valid_surface(const mfk_surface &surface)
{
    return surface.size == sizeof(surface) && surface.abi == MFK_ABI &&
        (display_window(static_cast<HWND>(surface.display_window)) ||
         (!surface.display_window && surface.visible == 0)) &&
        surface.width >= 0 && surface.height >= 0 &&
        surface.width <= D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION &&
        surface.height <= D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION &&
        std::isfinite(surface.scale_x) && surface.scale_x > 0 &&
        std::isfinite(surface.scale_y) && surface.scale_y > 0 &&
        std::isfinite(1.0f / surface.scale_x) && std::isfinite(1.0f / surface.scale_y) &&
        (surface.visible == 0 || surface.visible == 1);
}

bool font_attachment(const char *name, const char *mime)
{
    if (mime && (!_stricmp(mime, "application/x-truetype-font") ||
        !_stricmp(mime, "application/x-font-ttf") || !_stricmp(mime, "application/x-font-otf") ||
        !_stricmp(mime, "application/x-font") ||
        !_stricmp(mime, "application/vnd.ms-opentype") ||
        !_stricmp(mime, "application/font-sfnt") || !_stricmp(mime, "font/ttf") ||
        !_stricmp(mime, "font/otf") || !_stricmp(mime, "font/sfnt") ||
        !_stricmp(mime, "font/collection")))
        return true;
    const char *extension = name ? strrchr(name, '.') : nullptr;
    return extension && (!_stricmp(extension, ".ttf") || !_stricmp(extension, ".otf") ||
                         !_stricmp(extension, ".ttc") || !_stricmp(extension, ".otc"));
}

std::string ass_text(const char *text, size_t length)
{
    std::string escaped;
    for (size_t n = 0; n < length; n++) {
        switch (text[n]) {
        case '\r': break;
        case '\n': escaped += "\\N"; break;
        // ASS has no literal backslash escape. A word joiner breaks sequences
        // such as \N without a visible glyph or a font/style override.
        case '\\': escaped += "\\\xE2\x81\xA0"; break;
        case '{': case '}': escaped += '\\'; escaped += text[n]; break;
        default: escaped += text[n]; break;
        }
    }
    return escaped;
}

bool subtitle_yuv(ASS_YCbCrMatrix matrix, const pl_color_repr &video, bool hdr,
                  pl_transform3x3 &encode)
{
    if (hdr || matrix == YCBCR_NONE || !pl_color_system_is_ycbcr_like(video.sys))
        return false;
    float kr = 0.299f, kb = 0.114f;
    bool full = false;
    switch (matrix) {
    case YCBCR_BT601_PC: full = true; break;
    case YCBCR_BT709_PC: full = true; [[fallthrough]];
    case YCBCR_BT709_TV: kr = 0.2126f; kb = 0.0722f; break;
    case YCBCR_SMPTE240M_PC: full = true; [[fallthrough]];
    case YCBCR_SMPTE240M_TV: kr = 0.2122f; kb = 0.0865f; break;
    case YCBCR_FCC_PC: full = true; [[fallthrough]];
    case YCBCR_FCC_TV: kr = 0.30f; kb = 0.11f; break;
    default: break; // Missing or unknown header means TV.601.
    }
    const float kg = 1 - kr - kb;
    const float y = full ? 1 : 219.0f / 255;
    const float c = full ? 254.0f / 255 : 224.0f / 255;
    encode = {};
    encode.mat = {{{y * kr, y * kg, y * kb},
                   {-c * kr / (2 * (1 - kb)), -c * kg / (2 * (1 - kb)), c / 2},
                   {c / 2, -c * kg / (2 * (1 - kr)), -c * kb / (2 * (1 - kr))}}};
    encode.c[0] = full ? 0 : 16.0f / 255;
    encode.c[1] = encode.c[2] = 128.0f / 255;
    return true;
}

bool milliseconds(double seconds, long long &value)
{
    if (!std::isfinite(seconds) || std::abs(seconds) >=
        static_cast<double>(std::numeric_limits<long long>::max() / 1000))
        return false;
    value = std::llround(seconds * 1000);
    return true;
}

const AVPixFmtDescriptor *frame_format(const AVFrame *frame)
{
    if (!frame || frame->width <= 0 || frame->height <= 0 ||
        frame->crop_left >= static_cast<size_t>(frame->width) ||
        frame->crop_right >= static_cast<size_t>(frame->width) ||
        frame->crop_top >= static_cast<size_t>(frame->height) ||
        frame->crop_bottom >= static_cast<size_t>(frame->height) ||
        frame->crop_left + frame->crop_right >= static_cast<size_t>(frame->width) ||
        frame->crop_top + frame->crop_bottom >= static_cast<size_t>(frame->height))
        return nullptr;
    auto format = static_cast<AVPixelFormat>(frame->format);
    const auto *desc = av_pix_fmt_desc_get(format);
    if (!desc) return nullptr;
    if (desc->flags & AV_PIX_FMT_FLAG_HWACCEL) {
        if (format != AV_PIX_FMT_D3D11 || !frame->hw_frames_ctx || !frame->data[0])
            return nullptr;
        const auto *frames = reinterpret_cast<const AVHWFramesContext *>(frame->hw_frames_ctx->data);
        if (frames->format != AV_PIX_FMT_D3D11 || !frames->device_ctx ||
            frames->device_ctx->type != AV_HWDEVICE_TYPE_D3D11VA)
            return nullptr;
        format = frames->sw_format;
        desc = av_pix_fmt_desc_get(format);
        if (!desc || (desc->flags & AV_PIX_FMT_FLAG_HWACCEL)) return nullptr;
    }
    const int planes = av_pix_fmt_count_planes(format);
    return planes > 0 && planes <= 4 ? desc : nullptr;
}

bool metadata_range(size_t size, size_t offset, size_t length)
{
    return offset >= sizeof(AVDOVIMetadata) && offset <= size && length <= size - offset;
}

int map_dovi(pl_frame &frame, pl_dovi_metadata &out, const AVFrame *input, const AVStream *stream)
{
    const auto *side = av_frame_get_side_data(input, AV_FRAME_DATA_DOVI_METADATA);
    if (!side) {
        if (av_frame_get_side_data(input, AV_FRAME_DATA_DOVI_RPU_BUFFER))
            return MFK_DOVI_UNSUPPORTED;
        if (stream && av_packet_side_data_get(stream->codecpar->coded_side_data,
            stream->codecpar->nb_coded_side_data, AV_PKT_DATA_DOVI_CONF))
            return MFK_DOVI_UNSUPPORTED; // Declared DV must not become BL-only HDR10.
        return MFK_OK;
    }
    if (!side->data || side->size < sizeof(AVDOVIMetadata)) return MFK_DOVI_UNSUPPORTED;
    const auto *metadata = reinterpret_cast<const AVDOVIMetadata *>(side->data);
    if (!metadata_range(side->size, metadata->header_offset, sizeof(AVDOVIRpuDataHeader)) ||
        metadata->header_offset % alignof(AVDOVIRpuDataHeader) ||
        !metadata_range(side->size, metadata->mapping_offset, sizeof(AVDOVIDataMapping)) ||
        metadata->mapping_offset % alignof(AVDOVIDataMapping) ||
        !metadata_range(side->size, metadata->color_offset, sizeof(AVDOVIColorMetadata)) ||
        metadata->color_offset % alignof(AVDOVIColorMetadata) ||
        metadata->num_ext_blocks < 0 || metadata->num_ext_blocks > AV_DOVI_MAX_EXT_BLOCKS ||
        (metadata->num_ext_blocks &&
         (metadata->ext_block_size < sizeof(AVDOVIDmData) ||
          metadata->ext_block_offset % alignof(AVDOVIDmData) ||
          metadata->ext_block_size % alignof(AVDOVIDmData) ||
          metadata->ext_block_size > side->size / metadata->num_ext_blocks ||
          !metadata_range(side->size, metadata->ext_block_offset,
                          metadata->ext_block_size * metadata->num_ext_blocks))))
        return MFK_DOVI_UNSUPPORTED;
    const auto *header = av_dovi_get_header(metadata);
    const auto *mapping = av_dovi_get_mapping(metadata);
    if (header->coef_log2_denom > 32 || header->bl_bit_depth < 8 ||
        header->bl_bit_depth > 16 || header->el_bit_depth < 8 || header->el_bit_depth > 16 ||
        (!header->disable_residual_flag && mapping->nlq_method_idc != AV_DOVI_NLQ_LINEAR_DZ))
        return MFK_DOVI_UNSUPPORTED;
    for (const auto &curve : mapping->curves) {
        if (curve.num_pivots < 2 || curve.num_pivots > 9) return MFK_DOVI_UNSUPPORTED;
        for (int i = 0; i < curve.num_pivots - 1; i++) {
            if ((curve.mapping_idc[i] != AV_DOVI_MAPPING_POLYNOMIAL &&
                 curve.mapping_idc[i] != AV_DOVI_MAPPING_MMR) ||
                (curve.mapping_idc[i] == AV_DOVI_MAPPING_POLYNOMIAL && curve.poly_order[i] > 2) ||
                (curve.mapping_idc[i] == AV_DOVI_MAPPING_MMR &&
                 (curve.mmr_order[i] < 1 || curve.mmr_order[i] > 3)))
                return MFK_DOVI_UNSUPPORTED;
        }
    }

    if (header->coef_log2_denom < 31) {
        pl_map_avdovi_metadata(&frame.color, &frame.repr, &out, metadata);
    } else {
        // v7.387's libav helper uses `1 << denom` for reshape coefficients.
        // FFmpeg legitimately emits denom=32 for float RPU coefficients.
        // Map a scratch header without undefined shifts, then restore scales.
        std::vector<uint8_t> scratch(side->data, side->data + side->size);
        const auto *safe = reinterpret_cast<const AVDOVIMetadata *>(scratch.data());
        av_dovi_get_header(safe)->coef_log2_denom = 0;
        pl_map_avdovi_metadata(&frame.color, &frame.repr, &out, safe);
        const double scale = std::ldexp(1.0, -header->coef_log2_denom);
        bool trivial = true;
        for (int c = 0; c < 3; c++) {
            const auto &curve = mapping->curves[c];
            auto &converted = out.comp[c];
            for (int i = 0; i < curve.num_pivots - 1; i++) {
                if (curve.mapping_idc[i] == AV_DOVI_MAPPING_POLYNOMIAL) {
                    for (int k = 0; k <= curve.poly_order[i]; k++)
                        converted.poly_coeffs[i][k] = static_cast<float>(scale * curve.poly_coef[i][k]);
                } else {
                    converted.mmr_constant[i] = static_cast<float>(scale * curve.mmr_constant[i]);
                    for (int j = 0; j < curve.mmr_order[i]; j++)
                        for (int k = 0; k < 7; k++)
                            converted.mmr_coeffs[i][j][k] = static_cast<float>(scale * curve.mmr_coef[i][j][k]);
                }
            }
            const auto &nlq = mapping->nlq[c];
            trivial &= nlq.nlq_offset == 0 &&
                nlq.vdr_in_max == (uint64_t{1} << header->coef_log2_denom) &&
                nlq.linear_deadzone_slope == 0 && nlq.linear_deadzone_threshold == 0;
            out.nlq[c].offset = nlq.nlq_offset;
            out.nlq[c].deadzone_slope = static_cast<float>(scale * nlq.linear_deadzone_slope);
            out.nlq[c].deadzone_threshold = static_cast<float>(scale * nlq.linear_deadzone_threshold);
            out.nlq[c].hdr_in_max = static_cast<float>(scale * nlq.vdr_in_max);
        }
        out.nlq_active = !header->disable_residual_flag &&
            mapping->nlq_method_idc == AV_DOVI_NLQ_LINEAR_DZ && !trivial;
    }
    return MFK_OK;
}

bool advanced_color_enabled(HMONITOR monitor)
{
    MONITORINFOEXW info{};
    info.cbSize = sizeof(info);
    if (!GetMonitorInfoW(monitor, &info)) return false;
    UINT32 path_count = 0, mode_count = 0;
    std::vector<DISPLAYCONFIG_PATH_INFO> paths;
    std::vector<DISPLAYCONFIG_MODE_INFO> modes;
    LONG status;
    // Display topology can change between the size query and the snapshot.
    do {
        status = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &path_count, &mode_count);
        if (status != ERROR_SUCCESS) return false;
        paths.resize(path_count); modes.resize(mode_count);
        status = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &path_count, paths.data(),
                                    &mode_count, modes.data(), nullptr);
    } while (status == ERROR_INSUFFICIENT_BUFFER);
    if (status != ERROR_SUCCESS) return false;
    bool found = false;
    for (UINT32 i = 0; i < path_count; i++) {
        DISPLAYCONFIG_SOURCE_DEVICE_NAME source{};
        source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        source.header.size = sizeof(source);
        source.header.adapterId = paths[i].sourceInfo.adapterId;
        source.header.id = paths[i].sourceInfo.id;
        if (DisplayConfigGetDeviceInfo(&source.header) != ERROR_SUCCESS ||
            wcscmp(source.viewGdiDeviceName, info.szDevice) != 0)
            continue;
        DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO color{};
        color.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
        color.header.size = sizeof(color);
        color.header.adapterId = paths[i].targetInfo.adapterId;
        color.header.id = paths[i].targetInfo.id;
        // In clone mode every target on this source must support active HDR.
        if (DisplayConfigGetDeviceInfo(&color.header) != ERROR_SUCCESS ||
            !color.advancedColorSupported || !color.advancedColorEnabled ||
            color.advancedColorForceDisabled)
            return false;
        found = true;
    }
    return found;
}

struct MappedFrame {
    pl_gpu gpu;
    pl_frame frame{};
    pl_dovi_metadata dovi{};
    bool adapter = false;
    pl_tex wrapped[2]{};
    explicit MappedFrame(pl_gpu value) : gpu(value) { }
    ~MappedFrame()
    {
        if (adapter) pl_unmap_avframe(gpu, &frame);
        for (auto &texture : wrapped) pl_tex_destroy(gpu, &texture);
    }
};

struct PendingSubmission {
    pl_swapchain swapchain;
    bool pending = true;
    ~PendingSubmission() { if (pending) pl_swapchain_submit_frame(swapchain); }
    bool submit() { pending = false; return pl_swapchain_submit_frame(swapchain); }
};

struct SubtitleOverlays {
    pl_gpu gpu;
    size_t bytes = 0;
    std::vector<pl_overlay> overlays;
    std::vector<pl_overlay_part> parts;
    std::vector<pl_tex> textures;
    explicit SubtitleOverlays(pl_gpu value) : gpu(value) { }
    ~SubtitleOverlays()
    {
        for (auto &texture : textures) pl_tex_destroy(gpu, &texture);
    }
    bool add(const uint8_t *pixels, int w, int h, int stride, pl_rect2df dst,
             bool mono, uint32_t color, const pl_color_space &space)
    {
        if (w <= 0 || h <= 0) return true;
        if (!pixels) return false;
        const size_t row = static_cast<size_t>(w) * (mono ? 1 : 4);
        if (stride < 0 || static_cast<size_t>(stride) < row ||
            row > max_subtitle_bytes / h || bytes > max_subtitle_bytes - row * h ||
            textures.size() >= 4096)
            return false;
        pl_fmt format = pl_find_fmt(gpu, PL_FMT_UNORM, mono ? 1 : 4, 8, 8,
                                   static_cast<pl_fmt_caps>(PL_FMT_CAP_SAMPLEABLE | PL_FMT_CAP_LINEAR));
        if (!format) return false;
        pl_tex_params parameters{};
        parameters.w = w; parameters.h = h; parameters.format = format;
        parameters.sampleable = true; parameters.host_writable = true;
        textures.push_back(nullptr);
        pl_tex texture = pl_tex_create(gpu, &parameters);
        textures.back() = texture;
        if (!texture) return false;
        bytes += row * h;
        // libass does not promise stride padding on the last row.
        std::vector<uint8_t> packed(row * h);
        for (int y = 0; y < h; y++)
            memcpy(packed.data() + row * y, pixels + static_cast<size_t>(stride) * y, row);
        pl_tex_transfer_params transfer{};
        transfer.tex = texture; transfer.ptr = packed.data(); transfer.row_pitch = row;
        if (!pl_tex_upload(gpu, &transfer)) return false;
        pl_overlay_part part{};
        part.src = {0, 0, static_cast<float>(w), static_cast<float>(h)};
        part.dst = dst;
        part.color[0] = ((color >> 24) & 255) / 255.0f;
        part.color[1] = ((color >> 16) & 255) / 255.0f;
        part.color[2] = ((color >> 8) & 255) / 255.0f;
        part.color[3] = 1 - (color & 255) / 255.0f;
        parts.push_back(part);
        pl_overlay item{};
        item.tex = texture;
        item.mode = mono ? PL_OVERLAY_MONOCHROME : PL_OVERLAY_NORMAL;
        item.coords = PL_OVERLAY_COORDS_DST_FRAME;
        item.repr = pl_color_repr_rgb;
        item.repr.alpha = PL_ALPHA_INDEPENDENT;
        item.color = space;
        item.num_parts = 1;
        overlays.push_back(item);
        return true;
    }
    void apply(pl_frame &target)
    {
        for (size_t i = 0; i < overlays.size(); i++) overlays[i].parts = &parts[i];
        target.overlays = overlays.data();
        target.num_overlays = static_cast<int>(overlays.size());
    }
};
}

struct FilmRenderer::Impl {
    mfk_surface surface{};
    HMONITOR monitor = nullptr;
    IDXGIFactory1 *factory = nullptr;
    IDXGIOutput6 *display = nullptr;
    DXGI_OUTPUT_DESC1 display_info{};
    pl_d3d11 device = nullptr;
    ID3D11DeviceContext *context = nullptr;
    ID3D11Query *completion = nullptr;
    pl_renderer renderer = nullptr;
    pl_swapchain swapchain = nullptr;
    IDXGISwapChain3 *composition = nullptr;
    AVBufferRef *hardware = nullptr;
    const AVStream *stream = nullptr;
    std::recursive_mutex gpu_lock;
    ASS_Library *ass = nullptr;
    ASS_Renderer *ass_renderer = nullptr;
    ASS_Track *ass_track = nullptr;
    bool hdr = false, dovi = false, lost = false, bitmap_subtitles = false;
    std::atomic<uint64_t> submitted{0};
    int subtitle_width = 0, subtitle_height = 0, text_order = 0;
    double subtitle_position = -std::numeric_limits<double>::infinity();

    struct FrameTextures {
        pl_tex software[4]{}, copy_planes[2]{};
        ID3D11Texture2D *copy = nullptr;
        void clear(pl_gpu gpu)
        {
            for (auto &texture : software) pl_tex_destroy(gpu, &texture);
            for (auto &texture : copy_planes) pl_tex_destroy(gpu, &texture);
            release(copy);
        }
    } textures, el_textures;
    struct Bitmap {
        double start, end;
        int x, y, width, height, canvas_width, canvas_height;
        std::vector<uint8_t> rgba;
    };
    std::deque<Bitmap> bitmaps;
    size_t bitmap_bytes = 0, font_bytes = 0;

    static void lock_device(void *opaque) { static_cast<Impl *>(opaque)->gpu_lock.lock(); }
    static void unlock_device(void *opaque) { static_cast<Impl *>(opaque)->gpu_lock.unlock(); }

    int device_status(int fallback = MFK_OK)
    {
        if (device && (FAILED(device->device->GetDeviceRemovedReason()) ||
                       pl_gpu_is_failed(device->gpu)))
            lost = true;
        return lost ? MFK_DEVICE_LOST : fallback;
    }

    int drain()
    {
        if (!device || !context || !completion) return MFK_OK;
        if (device_status() != MFK_OK) return MFK_DEVICE_LOST;
        context->End(completion);
        context->Flush();
        HRESULT status;
        while ((status = context->GetData(completion, nullptr, 0, 0)) == S_FALSE) {
            if (device_status() != MFK_OK) return MFK_DEVICE_LOST;
            Sleep(1);
        }
        return device_status(FAILED(status) ? MFK_RENDER_FAILED : MFK_OK);
    }

    ~Impl()
    {
        std::lock_guard guard(gpu_lock);
        // An actual completion query also terminates on removal; libplacebo's
        // fence-based finish can otherwise wait indefinitely on a lost device.
        drain();
        if (context) context->ClearState();
        if (device) { textures.clear(device->gpu); el_textures.clear(device->gpu); }
        if (ass_track) ass_free_track(ass_track);
        if (ass_renderer) ass_renderer_done(ass_renderer);
        if (ass) ass_library_done(ass);
        av_buffer_unref(&hardware);
        pl_swapchain_destroy(&swapchain);
        release(composition);
        pl_renderer_destroy(&renderer);
        release(completion); release(context);
        pl_d3d11_destroy(&device);
        release(display); release(factory);
    }

    bool read_display(HWND target)
    {
        const auto current = MonitorFromWindow(target, MONITOR_DEFAULTTONEAREST);
        if (!factory || !factory->IsCurrent() || current != monitor || !display) {
            release(display); release(factory);
            monitor = current;
            if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory1),
                                          reinterpret_cast<void **>(&factory))))
                return false;
            for (UINT a = 0; !display; a++) {
                IDXGIAdapter1 *adapter = nullptr;
                if (FAILED(factory->EnumAdapters1(a, &adapter))) break;
                for (UINT o = 0; !display; o++) {
                    IDXGIOutput *output = nullptr;
                    if (FAILED(adapter->EnumOutputs(o, &output))) break;
                    DXGI_OUTPUT_DESC desc{};
                    if (SUCCEEDED(output->GetDesc(&desc)) && desc.Monitor == monitor)
                        output->QueryInterface(__uuidof(IDXGIOutput6),
                                               reinterpret_cast<void **>(&display));
                    release(output);
                }
                release(adapter);
            }
        }
        display_info = {};
        return display && SUCCEEDED(display->GetDesc1(&display_info)) &&
            display_info.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 &&
            display_info.BitsPerColor >= 10 && advanced_color_enabled(monitor);
    }

    int describe(const AVFrame *frame, pl_frame &out, pl_dovi_metadata &metadata, bool base)
    {
        if (!frame_format(frame)) return MFK_FORMAT_UNSUPPORTED;
        AVFrame effective = *frame;
        if (base && stream) {
            const auto *codec = stream->codecpar;
            if (effective.color_trc == AVCOL_TRC_UNSPECIFIED) effective.color_trc = codec->color_trc;
            if (effective.color_primaries == AVCOL_PRI_UNSPECIFIED) effective.color_primaries = codec->color_primaries;
            if (effective.colorspace == AVCOL_SPC_UNSPECIFIED) effective.colorspace = codec->color_space;
            if (effective.color_range == AVCOL_RANGE_UNSPECIFIED) effective.color_range = codec->color_range;
        }
        pl_frame_from_avframe(&out, &effective);
        if (base && stream) {
            pl_frame properties{};
            pl_frame_copy_stream_props(&properties, stream);
            // Dynamic frame metadata wins over container defaults.
            pl_hdr_metadata_merge(&out.color.hdr, &properties.color.hdr);
            if (!av_frame_get_side_data(frame, AV_FRAME_DATA_DISPLAYMATRIX))
                out.rotation = properties.rotation;
            if (effective.sample_aspect_ratio.num <= 0 || effective.sample_aspect_ratio.den <= 0)
                out.pixel_aspect_ratio = properties.pixel_aspect_ratio;
        }
        return base ? map_dovi(out, metadata, frame, stream) : MFK_OK;
    }

    int hardware_texture(const AVFrame *frame, ID3D11Texture2D *&texture,
                         D3D11_TEXTURE2D_DESC &desc, int &slice)
    {
        texture = reinterpret_cast<ID3D11Texture2D *>(frame->data[0]);
        ID3D11Device *owner = nullptr;
        texture->GetDevice(&owner);
        const bool shared = owner == device->device;
        release(owner);
        if (!shared) return MFK_RENDER_FAILED;
        texture->GetDesc(&desc);
        const auto index = reinterpret_cast<intptr_t>(frame->data[1]);
        if (index < 0 || static_cast<uintptr_t>(index) >= desc.ArraySize ||
            desc.MipLevels != 1 || desc.SampleDesc.Count != 1 ||
            desc.Usage != D3D11_USAGE_DEFAULT ||
            desc.Width < static_cast<UINT>(frame->width) ||
            desc.Height < static_cast<UINT>(frame->height))
            return MFK_RENDER_FAILED;
        slice = static_cast<int>(index);
        return device_status();
    }

    bool wrap_planes(ID3D11Texture2D *texture, const D3D11_TEXTURE2D_DESC &desc,
                     int slice, pl_tex planes[2])
    {
        for (int i = 0; i < 2; i++) {
            pl_d3d11_wrap_params parameters{};
            parameters.tex = texture;
            parameters.array_slice = slice;
            const bool eight = desc.Format == DXGI_FORMAT_NV12;
            parameters.fmt = eight ? (i ? DXGI_FORMAT_R8G8_UNORM : DXGI_FORMAT_R8_UNORM) :
                                    (i ? DXGI_FORMAT_R16G16_UNORM : DXGI_FORMAT_R16_UNORM);
            parameters.w = static_cast<int>(desc.Width) / (i ? 2 : 1);
            parameters.h = static_cast<int>(desc.Height) / (i ? 2 : 1);
            planes[i] = pl_d3d11_wrap(device->gpu, &parameters);
            if (!planes[i] || !planes[i]->params.sampleable) {
                for (int n = 0; n <= i; n++) pl_tex_destroy(device->gpu, &planes[n]);
                return false;
            }
        }
        return true;
    }

    int map_frame(const AVFrame *input, MappedFrame &mapped, FrameTextures &storage, bool base)
    {
        int result = describe(input, mapped.frame, mapped.dovi, base);
        if (result != MFK_OK) return result;
        const pl_frame description = mapped.frame;
        AVFrame *download = nullptr;
        if (input->format == AV_PIX_FMT_D3D11) {
            ID3D11Texture2D *texture = nullptr;
            D3D11_TEXTURE2D_DESC desc{};
            int slice = 0;
            result = hardware_texture(input, texture, desc, slice);
            if (result != MFK_OK) return result;
            const auto *format = frame_format(input);
            const bool planar = (desc.Format == DXGI_FORMAT_NV12 ||
                                 desc.Format == DXGI_FORMAT_P010 || desc.Format == DXGI_FORMAT_P016) &&
                mapped.frame.num_planes == 2 && format->nb_components == 3 &&
                format->log2_chroma_w == 1 && format->log2_chroma_h == 1;
            if (planar) {
                pl_tex *planes = nullptr;
                const bool direct = (desc.BindFlags & D3D11_BIND_SHADER_RESOURCE) &&
                    wrap_planes(texture, desc, slice, mapped.wrapped);
                if (direct) planes = mapped.wrapped;
                else {
                    if (device_status() != MFK_OK) return MFK_DEVICE_LOST;
                    D3D11_TEXTURE2D_DESC previous{};
                    if (storage.copy) storage.copy->GetDesc(&previous);
                    if (!storage.copy || previous.Width != desc.Width ||
                        previous.Height != desc.Height || previous.Format != desc.Format) {
                        for (auto &plane : storage.copy_planes) pl_tex_destroy(device->gpu, &plane);
                        release(storage.copy);
                        auto copy_desc = desc;
                        copy_desc.ArraySize = 1;
                        copy_desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
                        copy_desc.MiscFlags = copy_desc.CPUAccessFlags = 0;
                        device->device->CreateTexture2D(&copy_desc, nullptr, &storage.copy);
                    }
                    if (storage.copy && (storage.copy_planes[0] ||
                        wrap_planes(storage.copy, desc, 0, storage.copy_planes))) {
                        context->CopySubresourceRegion(storage.copy, 0, 0, 0, 0,
                            texture, D3D11CalcSubresource(0, slice, desc.MipLevels), nullptr);
                        planes = storage.copy_planes;
                    }
                }
                if (planes) {
                    for (int i = 0; i < 2; i++) mapped.frame.planes[i].texture = planes[i];
                    mapped.frame.repr.bits.sample_depth = planes[0]->params.format->component_depth[0];
                    mapped.frame.repr.bits.color_depth = format->comp[0].depth;
                    mapped.frame.repr.bits.bit_shift = format->comp[0].shift;
                    return device_status();
                }
            }
            // v7.387's libav adapter has no D3D11 mapping. Other hardware
            // layouts or unsampleable textures transfer without SDR conversion.
            if (device_status() != MFK_OK) return MFK_DEVICE_LOST;
            download = av_frame_alloc();
            if (!download) return MFK_RENDER_FAILED;
            if (av_hwframe_transfer_data(download, input, 0) < 0 ||
                av_frame_copy_props(download, input) < 0) {
                av_frame_free(&download);
                return device_status(MFK_RENDER_FAILED);
            }
        }
        pl_avframe_params parameters{};
        parameters.frame = download ? download : input;
        parameters.tex = storage.software;
        // describe() maps DOVI for both software and D3D11 frames, including
        // the fixed adapter's denominator=32 correction.
        parameters.map_dovi = false;
        const bool success = pl_map_avframe_ex(device->gpu, &mapped.frame, &parameters);
        av_frame_free(&download);
        if (!success) return device_status(MFK_RENDER_FAILED);
        mapped.adapter = true;
        const auto bits = mapped.frame.repr.bits;
        mapped.frame.color = description.color;
        mapped.frame.repr = description.repr;
        mapped.frame.repr.bits = bits;
        mapped.frame.crop = description.crop;
        mapped.frame.rotation = description.rotation;
        mapped.frame.pixel_aspect_ratio = description.pixel_aspect_ratio;
        return device_status();
    }

    int check_output(const pl_swapchain_frame &output)
    {
        if (output.color_space.transfer != PL_COLOR_TRC_SCRGB ||
            output.color_space.primaries != PL_COLOR_PRIM_BT_709 ||
            output.fbo->params.format->type != PL_FMT_FLOAT ||
            output.fbo->params.format->num_components != 4 ||
            output.fbo->params.format->component_depth[0] != 16)
            return MFK_RENDER_FAILED;
        DXGI_SWAP_CHAIN_DESC1 desc{};
        UINT support = 0;
        const bool supported = composition && SUCCEEDED(composition->GetDesc1(&desc)) &&
            desc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT &&
            SUCCEEDED(composition->CheckColorSpaceSupport(
                DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709, &support)) &&
            (support & DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT);
        return device_status(supported ? MFK_OK : MFK_HDR_DISPLAY_UNSUPPORTED);
    }

    pl_hdr_metadata display_metadata() const
    {
        pl_hdr_metadata metadata{};
        const pl_raw_primaries primaries = {
            {display_info.RedPrimary[0], display_info.RedPrimary[1]},
            {display_info.GreenPrimary[0], display_info.GreenPrimary[1]},
            {display_info.BluePrimary[0], display_info.BluePrimary[1]},
            {display_info.WhitePoint[0], display_info.WhitePoint[1]},
        };
        if (pl_primaries_valid(&primaries)) metadata.prim = primaries;
        if (std::isfinite(display_info.MaxLuminance) && display_info.MaxLuminance > 0)
            metadata.max_luma = metadata.max_cll = display_info.MaxLuminance;
        if (std::isfinite(display_info.MinLuminance) && display_info.MinLuminance >= 0 &&
            display_info.MinLuminance < metadata.max_luma)
            metadata.min_luma = std::max(PL_COLOR_HDR_BLACK, display_info.MinLuminance);
        if (std::isfinite(display_info.MaxFullFrameLuminance) &&
            display_info.MaxFullFrameLuminance > 0 && metadata.max_luma > 0)
            metadata.max_fall = std::min(metadata.max_luma, display_info.MaxFullFrameLuminance);
        return metadata;
    }

    int configure_target(const mfk_surface &target)
    {
        if (!swapchain || !composition) return MFK_RENDER_FAILED;
        if (device_status() != MFK_OK) return MFK_DEVICE_LOST;
        int width = std::max(1, target.width), height = std::max(1, target.height);
        // v7.387 wraps the external chain and ResizeBuffers preserves its COM
        // identity. Never send format hints: the UI owns that same identity.
        if (!pl_swapchain_resize(swapchain, &width, &height))
            return device_status(MFK_RENDER_FAILED);
        DXGI_MATRIX_3X2_F matrix{};
        matrix._11 = 1.0f / target.scale_x;
        matrix._22 = 1.0f / target.scale_y;
        if (FAILED(composition->SetMatrixTransform(&matrix)))
            return device_status(MFK_RENDER_FAILED);
        surface = target;
        return device_status();
    }

    int create_swapchain(const mfk_surface &target, void **result)
    {
        if (!device || !renderer) return MFK_RENDER_FAILED;
        if (swapchain || composition) return MFK_INVALID_REQUEST;
        if (device_status() != MFK_OK) return MFK_DEVICE_LOST;
        UINT format_support = 0;
        constexpr UINT required = D3D11_FORMAT_SUPPORT_TEXTURE2D | D3D11_FORMAT_SUPPORT_DISPLAY |
            D3D11_FORMAT_SUPPORT_SHADER_SAMPLE | D3D11_FORMAT_SUPPORT_RENDER_TARGET |
            D3D11_FORMAT_SUPPORT_BLENDABLE;
        if (FAILED(device->device->CheckFormatSupport(DXGI_FORMAT_R16G16B16A16_FLOAT, &format_support)) ||
            (format_support & required) != required)
            return device_status(MFK_RENDER_FAILED);
        IDXGIDevice *dxgi = nullptr;
        IDXGIAdapter *adapter = nullptr;
        IDXGIFactory2 *creator = nullptr;
        HRESULT status = device->device->QueryInterface(__uuidof(IDXGIDevice),
                                                       reinterpret_cast<void **>(&dxgi));
        if (SUCCEEDED(status)) status = dxgi->GetAdapter(&adapter);
        if (SUCCEEDED(status)) status = adapter->GetParent(__uuidof(IDXGIFactory2),
                                                         reinterpret_cast<void **>(&creator));
        release(adapter); release(dxgi);
        if (FAILED(status)) { release(creator); return device_status(MFK_RENDER_FAILED); }
        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width = std::max(1, target.width);
        desc.Height = std::max(1, target.height);
        desc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT | DXGI_USAGE_SHADER_INPUT |
            DXGI_USAGE_UNORDERED_ACCESS;
        desc.BufferCount = 2;
        desc.Scaling = DXGI_SCALING_STRETCH;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        IDXGISwapChain1 *native = nullptr;
        status = creator->CreateSwapChainForComposition(device->device, &desc, nullptr, &native);
        release(creator);
        if (SUCCEEDED(status))
            status = native->QueryInterface(__uuidof(IDXGISwapChain3),
                                           reinterpret_cast<void **>(&composition));
        UINT support = 0;
        if (SUCCEEDED(status))
            status = composition->CheckColorSpaceSupport(DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709,
                                                         &support);
        if (SUCCEEDED(status) && !(support & DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT))
            status = DXGI_ERROR_UNSUPPORTED;
        if (SUCCEEDED(status))
            status = composition->SetColorSpace1(DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709);
        if (FAILED(status)) {
            release(composition); release(native);
            return device_status(status == DXGI_ERROR_UNSUPPORTED ?
                MFK_HDR_DISPLAY_UNSUPPORTED : MFK_RENDER_FAILED);
        }
        pl_d3d11_swapchain_params parameters{};
        parameters.swapchain = native;
        // v7.387 selects initial color configuration even for external chains.
        // Request FP16 there too, then verify rather than accepting a downgrade.
        parameters.color_bits = parameters.alpha_bits = 16;
        swapchain = pl_d3d11_create_swapchain(device, &parameters);
        int code = swapchain ? configure_target(target) : device_status(MFK_RENDER_FAILED);
        if (code == MFK_OK) {
            pl_swapchain_frame output{};
            if (!pl_swapchain_start_frame(swapchain, &output))
                code = device_status(MFK_RENDER_FAILED);
            else {
                code = check_output(output);
                if (!pl_swapchain_submit_frame(swapchain)) code = device_status(MFK_RENDER_FAILED);
            }
        }
        if (code != MFK_OK) {
            pl_swapchain_destroy(&swapchain);
            release(composition); release(native);
            surface = {};
        } else {
            // Transfer CreateSwapChainForComposition's IDXGISwapChain1 reference.
            *result = native;
        }
        return code;
    }

    void prune_subtitles(double position)
    {
        for (auto it = bitmaps.begin(); it != bitmaps.end();) {
            if (it->end <= position) {
                bitmap_bytes -= it->rgba.size();
                it = bitmaps.erase(it);
            } else ++it;
        }
        long long time;
        if (ass_track && milliseconds(position, time)) ass_prune_events(ass_track, time);
    }
};

FilmRenderer::FilmRenderer() : impl(std::make_unique<Impl>()) { }
FilmRenderer::~FilmRenderer() = default;

int FilmRenderer::open(void *window)
{
    auto &p = *impl;
    std::lock_guard guard(p.gpu_lock);
    if (p.device) return MFK_INVALID_REQUEST;
    const auto target = static_cast<HWND>(window);
    if (target && !display_window(target)) return MFK_INVALID_REQUEST;
    pl_d3d11_params parameters = pl_d3d11_default_params;
    parameters.allow_software = false;
    parameters.min_feature_level = D3D_FEATURE_LEVEL_11_0;
    parameters.flags = D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    // Choose the window's output adapter before sharing the device with FFmpeg.
    // A later monitor move keeps that same decoder/render device and lets DWM
    // compose across adapters, subject to the target's actual HDR support.
    if (target) p.read_display(target);
    IDXGIAdapter *adapter = nullptr;
    if (p.display)
        p.display->GetParent(__uuidof(IDXGIAdapter), reinterpret_cast<void **>(&adapter));
    parameters.adapter = adapter;
    p.device = pl_d3d11_create(nullptr, &parameters);
    release(adapter);
    if (!p.device) return MFK_RENDER_FAILED;
    p.device->device->GetImmediateContext(&p.context);
    ID3D10Multithread *multithread = nullptr;
    if (!p.context || FAILED(p.context->QueryInterface(__uuidof(ID3D10Multithread),
                                       reinterpret_cast<void **>(&multithread))))
        return MFK_RENDER_FAILED;
    multithread->SetMultithreadProtected(TRUE);
    release(multithread);
    D3D11_QUERY_DESC completion{};
    completion.Query = D3D11_QUERY_EVENT;
    if (FAILED(p.device->device->CreateQuery(&completion, &p.completion)))
        return p.device_status(MFK_RENDER_FAILED);
    p.renderer = pl_renderer_create(nullptr, p.device->gpu);
    if (!p.renderer) return p.device_status(MFK_RENDER_FAILED);
    p.hardware = av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA);
    if (!p.hardware) return MFK_RENDER_FAILED;
    auto *context = reinterpret_cast<AVHWDeviceContext *>(p.hardware->data);
    auto *d3d = static_cast<AVD3D11VADeviceContext *>(context->hwctx);
    d3d->device = p.device->device;
    d3d->device->AddRef();
    d3d->device_context = p.context;
    d3d->device_context->AddRef();
    // Decoder texture arrays do not guarantee SRV support. Keep the decoder's
    // native allocation flags; map_frame verifies direct wrapping on the
    // actual resource and otherwise copies to a same-device sampleable texture.
    d3d->lock = Impl::lock_device;
    d3d->unlock = Impl::unlock_device;
    d3d->lock_ctx = &p;
    if (av_hwdevice_ctx_init(p.hardware) < 0) return p.device_status(MFK_RENDER_FAILED);
    p.ass = ass_library_init();
    if (!p.ass) return MFK_RENDER_FAILED;
    ass_set_message_cb(p.ass, ass_log, nullptr);
    p.ass_renderer = ass_renderer_init(p.ass);
    if (!p.ass_renderer) return MFK_RENDER_FAILED;
    ass_set_fonts(p.ass_renderer, nullptr, "Segoe UI", ASS_FONTPROVIDER_AUTODETECT, nullptr, 1);
    return p.device_status();
}

void FilmRenderer::set_stream(const AVStream *stream) { impl->stream = stream; }
AVBufferRef *FilmRenderer::hardware_device() const { return impl->hardware; }
int FilmRenderer::attach(const mfk_surface *surface, void **swapchain)
{
    if (!swapchain) return MFK_INVALID_REQUEST;
    *swapchain = nullptr;
    auto &p = *impl;
    std::lock_guard guard(p.gpu_lock);
    if (!surface) {
        int status = p.drain();
        if (p.context) p.context->ClearState();
        pl_swapchain_destroy(&p.swapchain);
        release(p.composition);
        p.surface = {};
        if (p.context) p.context->Flush();
        return status;
    }
    if (!valid_surface(*surface)) return MFK_INVALID_REQUEST;
    if (!display_window(static_cast<HWND>(surface->display_window))) return MFK_INVALID_REQUEST;
    if (p.device_status() != MFK_OK) return MFK_DEVICE_LOST;
    if (p.hdr && !p.read_display(static_cast<HWND>(surface->display_window)))
        return MFK_HDR_DISPLAY_UNSUPPORTED;
    return p.create_swapchain(*surface, swapchain);
}

int FilmRenderer::surface_update(const mfk_surface &surface)
{
    if (!valid_surface(surface)) return MFK_INVALID_REQUEST;
    auto &p = *impl;
    std::lock_guard guard(p.gpu_lock);
    if (p.hdr && surface.visible && surface.width > 0 && surface.height > 0 &&
        !p.read_display(static_cast<HWND>(surface.display_window)))
        return MFK_HDR_DISPLAY_UNSUPPORTED;
    return p.configure_target(surface);
}

int FilmRenderer::inspect(const AVFrame *base, const AVFrame *enhancement)
{
    if (!base) return MFK_INVALID_REQUEST;
    auto &p = *impl;
    std::lock_guard guard(p.gpu_lock);
    if (!p.device) return MFK_RENDER_FAILED;
    if (p.device_status() != MFK_OK) return MFK_DEVICE_LOST;
    pl_frame source{};
    pl_dovi_metadata metadata{};
    int status = p.describe(base, source, metadata, true);
    if (status != MFK_OK) return status;
    const bool dovi = source.repr.dovi != nullptr;
    const bool hdr = dovi || pl_color_transfer_is_hdr(source.color.transfer);
    if (hdr && frame_format(base)->comp[0].depth < 10) return MFK_FORMAT_UNSUPPORTED;
    if (dovi) {
        const auto *side = av_frame_get_side_data(base, AV_FRAME_DATA_DOVI_METADATA);
        const auto *header = av_dovi_get_header(reinterpret_cast<const AVDOVIMetadata *>(side->data));
        if (frame_format(base)->comp[0].depth != header->bl_bit_depth) return MFK_DOVI_UNSUPPORTED;
        if (metadata.nlq_active) {
            const auto *format = frame_format(enhancement);
            if (!format || format->nb_components < 3 ||
                format->comp[0].depth != header->el_bit_depth)
                return MFK_DOVI_UNSUPPORTED;
        }
    }
    for (const auto *frame : {base, enhancement}) if (frame) {
        if (!frame_format(frame)) return MFK_FORMAT_UNSUPPORTED;
        if (frame->format == AV_PIX_FMT_D3D11) {
            ID3D11Texture2D *texture = nullptr;
            D3D11_TEXTURE2D_DESC desc{};
            int slice;
            status = p.hardware_texture(frame, texture, desc, slice);
            if (status != MFK_OK) return status;
        }
    }
    p.dovi = dovi;
    p.hdr = hdr;
    // Silent candidates and invisible/zero-sized surfaces have no HDR output.
    // A monitor move or HDR setting change must be rechecked before presenting.
    if (p.hdr && p.swapchain && p.surface.visible &&
        p.surface.width > 0 && p.surface.height > 0 &&
        (!display_window(static_cast<HWND>(p.surface.display_window)) ||
         !p.read_display(static_cast<HWND>(p.surface.display_window))))
        return MFK_HDR_DISPLAY_UNSUPPORTED;
    return MFK_OK;
}

int FilmRenderer::present(const AVFrame *base, const AVFrame *enhancement,
                          double aspect, double position)
{
    auto &p = *impl;
    std::lock_guard guard(p.gpu_lock);
    long long time;
    if (!milliseconds(position, time) || !std::isfinite(aspect)) return MFK_INVALID_REQUEST;
    int status = inspect(base, enhancement);
    if (status != MFK_OK) return status;
    p.subtitle_position = position;
    p.prune_subtitles(position);
    if (!p.swapchain) return MFK_RENDER_FAILED;
    if (!p.surface.visible || p.surface.width == 0 || p.surface.height == 0)
        return MFK_OK;
    MappedFrame source(p.device->gpu), layer(p.device->gpu);
    status = p.map_frame(base, source, p.textures, true);
    if (status != MFK_OK) return status;
    if (source.frame.repr.dovi && source.dovi.nlq_active) {
        status = p.map_frame(enhancement, layer, p.el_textures, false);
        if (status != MFK_OK) return status;
        source.frame.enhancement_layer = &layer.frame;
    }
    pl_swapchain_frame output{};
    if (!pl_swapchain_start_frame(p.swapchain, &output))
        return p.device_status(MFK_RENDER_FAILED);
    PendingSubmission submission{p.swapchain};
    status = p.check_output(output);
    pl_frame target{};
    pl_frame_from_swapchain(&target, &output);
    const int width = output.fbo->params.w, height = output.fbo->params.h;
    if (p.hdr) target.color.hdr = p.display_metadata();
    const double source_width = source.frame.crop.x1 - source.frame.crop.x0;
    const double source_height = source.frame.crop.y1 - source.frame.crop.y0;
    const double pixel_aspect = source.frame.pixel_aspect_ratio > 0 ?
        source.frame.pixel_aspect_ratio : 1;
    double ratio = aspect > 0 ? aspect : source_width / source_height * pixel_aspect;
    if (aspect <= 0 && (static_cast<int>(source.frame.rotation) & 1)) ratio = 1 / ratio;
    if (ratio > static_cast<double>(width) / height) {
        const float h = static_cast<float>(width / ratio);
        target.crop.y0 = (height - h) / 2; target.crop.y1 = target.crop.y0 + h;
    } else {
        const float w = static_cast<float>(height * ratio);
        target.crop.x0 = (width - w) / 2; target.crop.x1 = target.crop.x0 + w;
    }
    SubtitleOverlays overlays(p.device->gpu);
    // HDR subtitles remain SDR reference white; SDR subtitles follow the
    // associated video's primaries and transfer, as required by libass.
    pl_color_space subtitle_space = p.hdr ? pl_color_space_srgb : source.frame.color;
    subtitle_space.hdr = {};
    if (status == MFK_OK && p.ass_track) {
        pl_transform3x3 encode{};
        const bool mangle = subtitle_yuv(p.ass_track->YCbCrMatrix, source.frame.repr, p.hdr, encode);
        ass_set_frame_size(p.ass_renderer, width, height);
        ass_set_storage_size(p.ass_renderer, base->width, base->height);
        ass_set_margins(p.ass_renderer, static_cast<int>(std::lround(target.crop.y0)),
            static_cast<int>(std::lround(height - target.crop.y1)),
            static_cast<int>(std::lround(target.crop.x0)),
            static_cast<int>(std::lround(width - target.crop.x1)));
        ass_set_use_margins(p.ass_renderer, 0);
        ass_set_pixel_aspect(p.ass_renderer, pixel_aspect);
        int changed = 0;
        for (auto *image = ass_render_frame(p.ass_renderer, p.ass_track, time, &changed);
             image; image = image->next) {
            if (!overlays.add(image->bitmap, image->w, image->h, image->stride,
                {static_cast<float>(image->dst_x), static_cast<float>(image->dst_y),
                 static_cast<float>(image->dst_x + image->w), static_cast<float>(image->dst_y + image->h)},
                true, image->color, subtitle_space)) {
                status = p.device_status(MFK_RENDER_FAILED); break;
            }
            if (mangle && image->w > 0 && image->h > 0) {
                // Preserve libass's legacy RGB -> header YUV -> video RGB
                // semantics. Let libplacebo decode the video representation,
                // including nonlinear systems, rather than approximating RGB.
                pl_transform3x3_apply(&encode, overlays.parts.back().color);
                auto &repr = overlays.overlays.back().repr;
                repr.sys = source.frame.repr.sys;
                repr.levels = source.frame.repr.levels;
                repr.bits = {8, 8, 0};
            }
        }
    }
    if (status == MFK_OK) for (const auto &bitmap : p.bitmaps) {
        if (bitmap.start > position || position >= bitmap.end) continue;
        const int canvas_width = bitmap.canvas_width > 0 ? bitmap.canvas_width : base->width;
        const int canvas_height = bitmap.canvas_height > 0 ? bitmap.canvas_height : base->height;
        // Source coordinates preserve crop, letterboxing, SAR, aspect overrides
        // and rotation, including a lower-resolution PGS canvas.
        const float sx = static_cast<float>(base->width) / canvas_width;
        const float sy = static_cast<float>(base->height) / canvas_height;
        if (!overlays.add(bitmap.rgba.data(), bitmap.width, bitmap.height, bitmap.width * 4,
            {bitmap.x * sx, bitmap.y * sy, (bitmap.x + bitmap.width) * sx, (bitmap.y + bitmap.height) * sy},
            false, 0, subtitle_space)) {
            status = p.device_status(MFK_RENDER_FAILED); break;
        }
        overlays.overlays.back().coords = PL_OVERLAY_COORDS_SRC_FRAME;
    }
    overlays.apply(target);
    pl_render_params parameters = pl_render_default_params;
    if (status == MFK_OK) {
        if (!pl_render_image(p.renderer, &source.frame, &target, &parameters))
            status = p.device_status(MFK_RENDER_FAILED);
        const auto errors = pl_renderer_get_errors(p.renderer).errors;
        // libplacebo may report success after falling back to BL or omitting
        // overlays. Such an incomplete frame must never reach Present.
        if (errors & (PL_RENDER_ERR_FBO | PL_RENDER_ERR_SAMPLING | PL_RENDER_ERR_OVERLAY |
                      PL_RENDER_ERR_BLENDING | PL_RENDER_ERR_FILM_GRAIN))
            status = p.device_status(MFK_RENDER_FAILED);
    }
    if (!submission.submit()) status = p.device_status(MFK_RENDER_FAILED);
    if (status == MFK_OK) pl_swapchain_swap_buffers(p.swapchain);
    // Both users submit to the same immediate context under the recursive
    // lock. Decoder reuse is ordered after these reads/copies; no borrowed
    // AVFrame escapes this call and no per-frame CPU wait is needed.
    pl_gpu_flush(p.device->gpu);
    status = p.device_status(status);
    if (status == MFK_OK) ++p.submitted;
    return status;
}

int FilmRenderer::subtitle_header(const AVCodecContext *codec)
{
    auto &p = *impl;
    std::lock_guard guard(p.gpu_lock);
    if (!codec || !p.ass) return MFK_INVALID_REQUEST;
    if (codec->subtitle_header_size > static_cast<int>(max_text_bytes)) return MFK_RENDER_FAILED;
    clear_subtitles();
    p.subtitle_width = codec->width; p.subtitle_height = codec->height;
    const auto *descriptor = avcodec_descriptor_get(codec->codec_id);
    p.bitmap_subtitles = descriptor && (descriptor->props & AV_CODEC_PROP_BITMAP_SUB);
    p.ass_track = ass_new_track(p.ass);
    if (!p.ass_track) return MFK_RENDER_FAILED;
    if (codec->subtitle_header && codec->subtitle_header_size > 0) {
        ass_process_codec_private(p.ass_track,
            reinterpret_cast<char *>(codec->subtitle_header), codec->subtitle_header_size);
    } else {
        char header[] =
            "[Script Info]\nScriptType: v4.00+\nPlayResX: 384\nPlayResY: 288\nYCbCr Matrix: None\n"
            "[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, "
            "OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, "
            "Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n"
            "Style: Default,Segoe UI,20,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,"
            "0,0,0,0,100,100,0,0,1,1,0,2,10,10,10,1\n"
            "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n";
        ass_process_codec_private(p.ass_track, header, sizeof(header) - 1);
    }
    return MFK_OK;
}

int FilmRenderer::subtitle(const AVSubtitle &subtitle, double pts, int canvas_width, int canvas_height)
{
    auto &p = *impl;
    std::lock_guard guard(p.gpu_lock);
    const double start = pts + subtitle.start_display_time / 1000.0;
    const double end = pts + subtitle.end_display_time / 1000.0;
    long long time, duration;
    if (!milliseconds(start, time) || !milliseconds(std::max(0.0, end - start), duration) ||
        subtitle.num_rects > max_subtitle_rects || (subtitle.num_rects && !subtitle.rects))
        return MFK_INVALID_REQUEST;
    p.prune_subtitles(p.subtitle_position);
    bool bitmap_packet = p.bitmap_subtitles;
    for (unsigned i = 0; i < subtitle.num_rects; i++) {
        if (!subtitle.rects[i]) return MFK_INVALID_REQUEST;
        bitmap_packet |= subtitle.rects[i]->type == SUBTITLE_BITMAP;
    }
    // Bitmap packets replace a complete display set; a zero-rect packet is
    // its explicit clear. Open-ended PGS cues live only until the next set.
    if (bitmap_packet)
        for (auto &bitmap : p.bitmaps) if (bitmap.start <= start) bitmap.end = std::min(bitmap.end, start);
    for (unsigned i = 0; i < subtitle.num_rects; i++) {
        const auto *rect = subtitle.rects[i];
        if ((rect->type == SUBTITLE_ASS && rect->ass) ||
            (rect->type == SUBTITLE_TEXT && rect->text)) {
            if (!p.ass_track) return MFK_RENDER_FAILED;
            if (duration <= 0) continue;
            const auto *text = rect->type == SUBTITLE_ASS ? rect->ass : rect->text;
            const size_t length = strnlen(text, max_text_bytes + 1);
            size_t bytes = length;
            for (int n = 0; n < p.ass_track->n_events && bytes <= max_text_bytes; n++)
                if (p.ass_track->events[n].Text) bytes += strlen(p.ass_track->events[n].Text);
            if (bytes > max_text_bytes || p.ass_track->n_events >= max_text_events)
                return MFK_RENDER_FAILED;
            std::string chunk;
            if (rect->type == SUBTITLE_TEXT) {
                // Keep both synthetic ReadOrder and libass's duplicate bitmap
                // bounded even for continuously active long-lived streams.
                while (p.ass_track->n_events && std::any_of(p.ass_track->events,
                    p.ass_track->events + p.ass_track->n_events,
                    [&](const ASS_Event &event) { return event.ReadOrder == p.text_order; }))
                    p.text_order = (p.text_order + 1) % (max_text_events + 1);
                chunk = std::to_string(p.text_order) + ",0,Default,,0,0,0,,";
                p.text_order = (p.text_order + 1) % (max_text_events + 1);
                chunk += ass_text(text, length);
            } else chunk.assign(text, length);
            if (chunk.size() > max_text_bytes - (bytes - length)) return MFK_RENDER_FAILED;
            ass_process_chunk(p.ass_track, chunk.data(), static_cast<int>(chunk.size()), time, duration);
        } else if (rect->type == SUBTITLE_BITMAP) {
            if (!rect->data[0] || !rect->data[1] || rect->w <= 0 || rect->h <= 0 ||
                rect->linesize[0] < rect->w || rect->nb_colors <= 0 || rect->nb_colors > 256)
                return MFK_INVALID_REQUEST;
            const uint64_t size = static_cast<uint64_t>(rect->w) * rect->h * 4;
            if (size > max_subtitle_bytes || p.bitmap_bytes > max_subtitle_bytes - size ||
                p.bitmaps.size() >= max_subtitle_rects)
                return MFK_RENDER_FAILED;
            Impl::Bitmap bitmap{start,
                subtitle.end_display_time == UINT32_MAX || end <= start ?
                    std::numeric_limits<double>::infinity() : end,
                rect->x, rect->y, rect->w, rect->h,
                canvas_width > 0 ? canvas_width : p.subtitle_width,
                canvas_height > 0 ? canvas_height : p.subtitle_height, {}};
            bitmap.rgba.resize(static_cast<size_t>(size));
            const auto *palette = reinterpret_cast<const uint32_t *>(rect->data[1]);
            for (int y = 0; y < rect->h; y++) for (int x = 0; x < rect->w; x++) {
                const unsigned index = rect->data[0][static_cast<size_t>(y) * rect->linesize[0] + x];
                const uint32_t color = index < static_cast<unsigned>(rect->nb_colors) ? palette[index] : 0;
                auto *pixel = &bitmap.rgba[(static_cast<size_t>(y) * rect->w + x) * 4];
                pixel[0] = (color >> 16) & 255; pixel[1] = (color >> 8) & 255;
                pixel[2] = color & 255; pixel[3] = color >> 24;
            }
            p.bitmap_bytes += bitmap.rgba.size();
            p.bitmaps.push_back(std::move(bitmap));
        }
    }
    return MFK_OK;
}

int FilmRenderer::add_attachment(const char *name, const char *mime, const uint8_t *bytes, size_t size)
{
    auto &p = *impl;
    std::lock_guard guard(p.gpu_lock);
    if (!font_attachment(name, mime)) return MFK_OK;
    if (!p.ass || !bytes || !size) return MFK_INVALID_REQUEST;
    if (size > max_subtitle_bytes || p.font_bytes > max_subtitle_bytes - size ||
        size > static_cast<size_t>(std::numeric_limits<int>::max()))
        return MFK_RENDER_FAILED;
    // libass copies the bytes into the library, independently of ASS_Track.
    ass_add_font(p.ass, name && *name ? name : "attachment",
                 reinterpret_cast<const char *>(bytes), static_cast<int>(size));
    p.font_bytes += size;
    ass_set_fonts(p.ass_renderer, nullptr, "Segoe UI", ASS_FONTPROVIDER_AUTODETECT, nullptr, 1);
    return MFK_OK;
}

void FilmRenderer::clear_subtitles()
{
    std::lock_guard guard(impl->gpu_lock);
    if (impl->ass_track) ass_free_track(impl->ass_track);
    impl->ass_track = nullptr; impl->bitmaps.clear(); impl->bitmap_bytes = 0;
    impl->subtitle_position = -std::numeric_limits<double>::infinity();
    impl->subtitle_width = impl->subtitle_height = impl->text_order = 0;
    impl->bitmap_subtitles = false;
}
int FilmRenderer::native_hdr() const { return impl->hdr; }
int FilmRenderer::dolby_vision() const { return impl->dovi; }
uint64_t FilmRenderer::submitted_pictures() const { return impl->submitted.load(); }
