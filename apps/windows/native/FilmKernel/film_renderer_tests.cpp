/* Standalone Windows x64 unit executable. Compile this translation unit
 * instead of film_renderer.cpp, plus film_libav.c as C, with the product's
 * locked headers, definitions and static libraries. Including the renderer
 * tests its actual internal conversions without a separate public test API.
 * The dummy GPU only checks host texture/overlay contracts: no window, shader,
 * D3D11VA, actual HDR display or whole-movie/FEL output is tested by the default
 * cases. --device additionally requires the logged-in desktop and exercises
 * the production D3D11 renderer, not a dummy GPU. */
#include "film_renderer.cpp"
#include <cstddef>
#include <cstdio>
#include <cstdlib>
#include <libplacebo/dummy.h>
extern "C" {
#include <libavutil/mem.h>
}

#define CHECK(condition) do { if (!(condition)) { \
    std::fprintf(stderr, "%s:%d: %s\n", __FILE__, __LINE__, #condition); \
    std::abort(); \
} } while (0)

namespace {
static_assert(MFK_ABI == 2 && sizeof(mfk_surface) == 40 && alignof(mfk_surface) == 8);
static_assert(offsetof(mfk_surface, size) == 0 && offsetof(mfk_surface, abi) == 4);
static_assert(offsetof(mfk_surface, display_window) == 8);
static_assert(offsetof(mfk_surface, width) == 16 && offsetof(mfk_surface, height) == 20);
static_assert(offsetof(mfk_surface, scale_x) == 24 && offsetof(mfk_surface, scale_y) == 28);
static_assert(offsetof(mfk_surface, visible) == 32);

bool near_value(float a, float b) { return std::abs(a - b) < 0.00001f; }

struct DoviFrame {
    AVFrame *frame = av_frame_alloc();
    AVFrameSideData *side = nullptr;
    AVDOVIMetadata *metadata = nullptr;
    explicit DoviFrame(int denominator)
    {
        CHECK(frame);
        size_t size;
        auto *allocated = av_dovi_metadata_alloc(&size);
        CHECK(allocated);
        side = av_frame_new_side_data(frame, AV_FRAME_DATA_DOVI_METADATA, size);
        CHECK(side);
        memcpy(side->data, allocated, size);
        av_free(allocated);
        metadata = reinterpret_cast<AVDOVIMetadata *>(side->data);
        auto *header = av_dovi_get_header(metadata);
        header->bl_bit_depth = header->el_bit_depth = 10;
        header->vdr_bit_depth = 12;
        header->coef_log2_denom = denominator;
        header->disable_residual_flag = 1;
        const int64_t unit = int64_t{1} << denominator;
        auto *mapping = av_dovi_get_mapping(metadata);
        mapping->nlq_method_idc = AV_DOVI_NLQ_LINEAR_DZ;
        for (auto &curve : mapping->curves) {
            curve.num_pivots = 2;
            curve.pivots[0] = 0; curve.pivots[1] = 1023;
            curve.mapping_idc[0] = AV_DOVI_MAPPING_POLYNOMIAL;
            curve.poly_order[0] = 2;
            curve.poly_coef[0][0] = -unit / 4;
            curve.poly_coef[0][1] = unit;
            curve.poly_coef[0][2] = unit / 2;
        }
        for (auto &nlq : mapping->nlq) nlq.vdr_in_max = unit;
        auto *color = av_dovi_get_color(metadata);
        for (auto &offset : color->ycc_to_rgb_offset) offset = {0, 1};
        for (int i = 0; i < 9; i++) {
            color->ycc_to_rgb_matrix[i] = {i % 4 == 0 ? 1 : 0, 1};
            color->rgb_to_lms_matrix[i] = {i % 4 == 0 ? 1 : 0, 1};
        }
        color->source_max_pq = 3079;
    }
    ~DoviFrame() { av_frame_free(&frame); }
};

void dovi_coefficients()
{
    for (int denominator : {13, 30, 31, 32}) {
        DoviFrame input(denominator);
        auto *mapping = av_dovi_get_mapping(input.metadata);
        auto &mmr = mapping->curves[1];
        const int64_t unit = int64_t{1} << denominator;
        mmr.mapping_idc[0] = AV_DOVI_MAPPING_MMR; mmr.mmr_order[0] = 3;
        mmr.mmr_constant[0] = unit / 4;
        mmr.mmr_coef[0][2][6] = -unit / 2;
        const std::vector<uint8_t> original(input.side->data, input.side->data + input.side->size);
        pl_frame frame{};
        pl_dovi_metadata output{};
        CHECK(map_dovi(frame, output, input.frame, nullptr) == MFK_OK);
        CHECK(frame.repr.dovi == &output && frame.repr.sys == PL_COLOR_SYSTEM_DOLBYVISION);
        CHECK(frame.color.transfer == PL_COLOR_TRC_PQ && frame.color.primaries == PL_COLOR_PRIM_BT_2020);
        CHECK(near_value(output.comp[0].poly_coeffs[0][0], -0.25f));
        CHECK(near_value(output.comp[0].poly_coeffs[0][1], 1));
        CHECK(near_value(output.comp[0].poly_coeffs[0][2], 0.5f));
        CHECK(near_value(output.comp[0].pivots[1], 1023.0f / 1024));
        CHECK(output.comp[1].mmr_order[0] == 3);
        CHECK(near_value(output.comp[1].mmr_constant[0], 0.25f));
        CHECK(near_value(output.comp[1].mmr_coeffs[0][2][6], -0.5f));
        CHECK(!output.nlq_active);
        CHECK(memcmp(original.data(), input.side->data, original.size()) == 0);
    }
}

void dovi_residual_and_rejection()
{
    for (int denominator : {13, 31, 32}) {
        DoviFrame input(denominator);
        auto *header = av_dovi_get_header(input.metadata);
        auto *mapping = av_dovi_get_mapping(input.metadata);
        header->disable_residual_flag = 0;
        pl_frame frame{};
        pl_dovi_metadata output{};
        CHECK(map_dovi(frame, output, input.frame, nullptr) == MFK_OK && !output.nlq_active);
        const uint64_t unit = uint64_t{1} << denominator;
        for (auto &nlq : mapping->nlq) {
            nlq.nlq_offset = 512;
            nlq.vdr_in_max = unit * 3 / 2;
            nlq.linear_deadzone_slope = unit / 8;
            nlq.linear_deadzone_threshold = unit / 4;
        }
        CHECK(map_dovi(frame, output, input.frame, nullptr) == MFK_OK && output.nlq_active);
        for (const auto &nlq : output.nlq) {
            CHECK(near_value(nlq.offset, 512) && near_value(nlq.hdr_in_max, 1.5f));
            CHECK(near_value(nlq.deadzone_slope, 0.125f) && near_value(nlq.deadzone_threshold, 0.25f));
        }
        mapping->nlq_method_idc = AV_DOVI_NLQ_NONE;
        CHECK(map_dovi(frame, output, input.frame, nullptr) == MFK_DOVI_UNSUPPORTED);
    }
    DoviFrame input(32);
    pl_frame frame{};
    pl_dovi_metadata output{};
    const auto offset = input.metadata->mapping_offset;
    input.metadata->mapping_offset++;
    CHECK(map_dovi(frame, output, input.frame, nullptr) == MFK_DOVI_UNSUPPORTED);
    input.metadata->mapping_offset = input.side->size;
    CHECK(map_dovi(frame, output, input.frame, nullptr) == MFK_DOVI_UNSUPPORTED);
    input.metadata->mapping_offset = offset;
    av_dovi_get_mapping(input.metadata)->curves[0].num_pivots = 10;
    CHECK(map_dovi(frame, output, input.frame, nullptr) == MFK_DOVI_UNSUPPORTED);
}

void missing_dovi_metadata()
{
    AVFrame *input = av_frame_alloc();
    CHECK(input);
    pl_frame frame{};
    pl_dovi_metadata output{};
    CHECK(map_dovi(frame, output, input, nullptr) == MFK_OK && !frame.repr.dovi);
    CHECK(av_frame_new_side_data(input, AV_FRAME_DATA_DOVI_RPU_BUFFER, 4));
    CHECK(map_dovi(frame, output, input, nullptr) == MFK_DOVI_UNSUPPORTED);
    av_frame_remove_side_data(input, AV_FRAME_DATA_DOVI_RPU_BUFFER);
    AVFormatContext *format = avformat_alloc_context();
    CHECK(format);
    AVStream *stream = avformat_new_stream(format, nullptr);
    CHECK(stream);
    auto *config = av_packet_side_data_new(&stream->codecpar->coded_side_data,
        &stream->codecpar->nb_coded_side_data, AV_PKT_DATA_DOVI_CONF,
        sizeof(AVDOVIDecoderConfigurationRecord), 0);
    CHECK(config);
    memset(config->data, 0, config->size);
    CHECK(map_dovi(frame, output, input, stream) == MFK_DOVI_UNSUPPORTED);
    reinterpret_cast<AVDOVIDecoderConfigurationRecord *>(config->data)->rpu_present_flag = 1;
    CHECK(map_dovi(frame, output, input, stream) == MFK_DOVI_UNSUPPORTED);
    avformat_free_context(format);
    av_frame_free(&input);
}

void hdr_frame_metadata()
{
    AVFrame *input = av_frame_alloc();
    CHECK(input);
    input->format = AV_PIX_FMT_YUV420P; input->width = 320; input->height = 180;
    input->color_primaries = AVCOL_PRI_BT2020; input->color_trc = AVCOL_TRC_SMPTE2084;
    input->colorspace = AVCOL_SPC_BT2020_NCL; input->color_range = AVCOL_RANGE_MPEG;
    auto *mastering = av_mastering_display_metadata_create_side_data(input);
    CHECK(mastering);
    mastering->has_luminance = 1;
    mastering->min_luminance = {1, 10000}; mastering->max_luminance = {1000, 1};
    auto *light = av_content_light_metadata_create_side_data(input);
    CHECK(light);
    light->MaxCLL = 1500; light->MaxFALL = 400;
    pl_frame output{};
    pl_frame_from_avframe(&output, input);
    CHECK(output.color.transfer == PL_COLOR_TRC_PQ && output.color.primaries == PL_COLOR_PRIM_BT_2020);
    CHECK(near_value(output.color.hdr.max_luma, 1000) && near_value(output.color.hdr.max_cll, 1500));
    auto *plus = av_dynamic_hdr_plus_create_side_data(input);
    CHECK(plus);
    plus->num_windows = 1;
    plus->targeted_system_display_maximum_luminance = {600, 1};
    auto &params = plus->params[0];
    for (auto &maximum : params.maxscl) maximum = {1, 10};
    params.average_maxrgb = {1, 50};
    params.tone_mapping_flag = 1;
    params.knee_point_x = {1, 2}; params.knee_point_y = {1, 3};
    params.num_bezier_curve_anchors = 2;
    params.bezier_curve_anchors[0] = {1, 4}; params.bezier_curve_anchors[1] = {3, 4};
    pl_frame_from_avframe(&output, input);
    CHECK(near_value(output.color.hdr.scene_max[0], 1000) && near_value(output.color.hdr.scene_avg, 200));
    CHECK(near_value(output.color.hdr.ootf.target_luma, 600) && output.color.hdr.ootf.num_anchors == 2);
    CHECK(near_value(output.color.hdr.ootf.anchors[1], 0.75f));
    av_frame_remove_side_data(input, AV_FRAME_DATA_DYNAMIC_HDR_PLUS);
    input->color_trc = AVCOL_TRC_ARIB_STD_B67; input->colorspace = AVCOL_SPC_ICTCP;
    pl_frame_from_avframe(&output, input);
    CHECK(output.color.transfer == PL_COLOR_TRC_HLG && output.repr.sys == PL_COLOR_SYSTEM_BT_2100_HLG);
    av_frame_free(&input);
}

void subtitle_colors_and_text()
{
    for (const auto matrix : {YCBCR_BT601_TV, YCBCR_BT601_PC, YCBCR_BT709_TV,
         YCBCR_BT709_PC, YCBCR_SMPTE240M_TV, YCBCR_SMPTE240M_PC}) {
        pl_color_repr video = pl_color_repr_sdtv;
        video.sys = matrix == YCBCR_BT709_TV || matrix == YCBCR_BT709_PC ? PL_COLOR_SYSTEM_BT_709 :
            matrix == YCBCR_SMPTE240M_TV || matrix == YCBCR_SMPTE240M_PC ?
                PL_COLOR_SYSTEM_SMPTE_240M : PL_COLOR_SYSTEM_BT_601;
        video.levels = matrix == YCBCR_BT601_PC || matrix == YCBCR_BT709_PC ||
            matrix == YCBCR_SMPTE240M_PC ? PL_COLOR_LEVELS_FULL : PL_COLOR_LEVELS_LIMITED;
        video.bits = {8, 8, 0};
        pl_transform3x3 encode{};
        CHECK(subtitle_yuv(matrix, video, false, encode));
        const auto decode = pl_color_repr_decode(&video, nullptr);
        for (auto original : {std::vector<float>{1, 0, 0}, {0, 1, 0}, {0, 0, 1}, {1, 1, 1}}) {
            auto converted = original;
            pl_transform3x3_apply(&encode, converted.data());
            pl_transform3x3_apply(&decode, converted.data());
            for (int i = 0; i < 3; i++) CHECK(near_value(converted[i], original[i]));
        }
    }
    pl_transform3x3 transform{};
    CHECK(!subtitle_yuv(YCBCR_NONE, pl_color_repr_sdtv, false, transform));
    CHECK(!subtitle_yuv(YCBCR_BT601_TV, pl_color_repr_rgb, false, transform));
    CHECK(!subtitle_yuv(YCBCR_BT601_TV, pl_color_repr_sdtv, true, transform));
    CHECK(subtitle_yuv(YCBCR_FCC_TV, pl_color_repr_sdtv, false, transform));
    float red[] = {1, 0, 0};
    pl_transform3x3_apply(&transform, red);
    CHECK(near_value(red[0], (16.0f + 219 * 0.30f) / 255));
    const char text[] = "line\r\n{tag}\\N";
    CHECK(ass_text(text, sizeof(text) - 1) == "line\\N\\{tag\\}\\\xE2\x81\xA0N");
    CHECK(font_attachment("font.OTF", nullptr));
    CHECK(font_attachment(nullptr, "FONT/SFNT"));
    CHECK(!font_attachment(nullptr, nullptr));
    FilmRenderer renderer;
    CHECK(renderer.add_attachment(nullptr, nullptr, nullptr, 0) == MFK_OK);
    CHECK(renderer.add_attachment("cover.jpg", "image/jpeg", nullptr, 0) == MFK_OK);
}

void subtitle_overlay_upload()
{
    pl_gpu gpu = pl_gpu_dummy_create(nullptr, nullptr);
    CHECK(gpu);
    {
        SubtitleOverlays overlays(gpu);
        // Exactly stride*(h-1)+w bytes: the final row has no padding.
        const uint8_t mono[] = {1, 2, 99, 99, 3, 4};
        CHECK(overlays.add(mono, 2, 2, 4, {5, 6, 7, 8}, true, 0x12345680, pl_color_space_srgb));
        const uint8_t packed[] = {1, 2, 3, 4};
        CHECK(memcmp(pl_tex_dummy_data(overlays.textures[0]), packed, sizeof(packed)) == 0);
        CHECK(overlays.textures[0]->params.format->host_bits[0] == 8);
        CHECK(overlays.overlays[0].repr.alpha == PL_ALPHA_INDEPENDENT);
        CHECK(near_value(overlays.parts[0].color[0], 0x12 / 255.0f));
        CHECK(near_value(overlays.parts[0].color[3], 127.0f / 255));
        const uint8_t rgba[] = {255, 0, 0, 64};
        CHECK(overlays.add(rgba, 1, 1, 4, {0, 0, 1, 1}, false, 0, pl_color_space_srgb));
        CHECK(memcmp(pl_tex_dummy_data(overlays.textures[1]), rgba, sizeof(rgba)) == 0);
        CHECK(overlays.overlays[1].mode == PL_OVERLAY_NORMAL);
        pl_frame target{};
        overlays.apply(target);
        CHECK(target.num_overlays == 2 && target.overlays[0].parts == &overlays.parts[0]);
        CHECK(target.overlays[0].parts[0].dst.x0 == 5);
        CHECK(!overlays.add(mono, 2, 2, 1, {}, true, 0, pl_color_space_srgb));
        overlays.bytes = max_subtitle_bytes;
        CHECK(!overlays.add(mono, 1, 1, 1, {}, true, 0, pl_color_space_srgb));
    }
    pl_gpu_dummy_destroy(&gpu);
}

void device_rendering()
{
    DWORD session = 0;
    CHECK(ProcessIdToSessionId(GetCurrentProcessId(), &session) && session != 0);
    HANDLE token = nullptr;
    CHECK(OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token));
    TOKEN_ELEVATION elevation{}; DWORD bytes = 0;
    CHECK(GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &bytes));
    CloseHandle(token);
    CHECK(!elevation.TokenIsElevated);
    const HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    CHECK(SUCCEEDED(apartment));
    WNDCLASSW type{};
    type.lpfnWndProc = DefWindowProcW; type.hInstance = GetModuleHandleW(nullptr);
    type.lpszClassName = L"MeowFilmRendererValidation";
    CHECK(RegisterClassW(&type));
    HWND window = CreateWindowExW(WS_EX_NOACTIVATE, type.lpszClassName,
        L"Meowfilm renderer validation", WS_OVERLAPPEDWINDOW, CW_USEDEFAULT,
        CW_USEDEFAULT, 480, 320, nullptr, nullptr, type.hInstance, nullptr);
    CHECK(window);
    ShowWindow(window, SW_SHOWNOACTIVATE); UpdateWindow(window);
    CHECK(IsWindowVisible(window));
    {
        FilmRenderer renderer;
        CHECK(renderer.open(nullptr) == MFK_OK);
        auto *hardware = renderer.hardware_device();
        CHECK(hardware);
        auto *device = reinterpret_cast<AVD3D11VADeviceContext *>(
            reinterpret_cast<AVHWDeviceContext *>(hardware->data)->hwctx)->device;
        CHECK(device && SUCCEEDED(device->GetDeviceRemovedReason()));
        IDXGIDevice *dxgi = nullptr; IDXGIAdapter *adapter = nullptr;
        CHECK(SUCCEEDED(device->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void **>(&dxgi))));
        CHECK(SUCCEEDED(dxgi->GetAdapter(&adapter)));
        DXGI_ADAPTER_DESC description{}; CHECK(SUCCEEDED(adapter->GetDesc(&description)));
        std::printf("D3D11 device vendor=%04X device=%04X desktop_session=%lu\n",
                    description.VendorId, description.DeviceId, session);
        release(adapter); release(dxgi);
        AVFrame *frame = av_frame_alloc(); CHECK(frame);
        frame->format = AV_PIX_FMT_YUV420P; frame->width = 160; frame->height = 90;
        frame->color_primaries = AVCOL_PRI_BT709; frame->color_trc = AVCOL_TRC_BT709;
        frame->colorspace = AVCOL_SPC_BT709; frame->color_range = AVCOL_RANGE_MPEG;
        CHECK(av_frame_get_buffer(frame, 32) == 0);
        for (int plane = 0; plane < 3; plane++)
            for (int y = 0; y < (plane ? 45 : 90); y++)
                memset(frame->data[plane] + y * frame->linesize[plane],
                       plane ? 128 : 160, plane ? 80 : 160);
        CHECK(renderer.inspect(frame, nullptr) == MFK_OK && !renderer.native_hdr());
        mfk_surface surface{sizeof(mfk_surface), MFK_ABI, window, 480, 320, 1, 1, 1};
        void *target = nullptr;
        auto hidden = surface;
        hidden.display_window = nullptr; hidden.visible = 0;
        CHECK(valid_surface(hidden));
        CHECK(renderer.attach(&hidden, &target) == MFK_INVALID_REQUEST && !target);
        hidden.visible = 1;
        CHECK(!valid_surface(hidden));
        CHECK(renderer.attach(&surface, nullptr) == MFK_INVALID_REQUEST);
        CHECK(renderer.attach(&surface, &target) == MFK_OK && target);
        auto *swapchain = static_cast<IDXGISwapChain1 *>(target);
        void *duplicate = target;
        CHECK(renderer.attach(&surface, &duplicate) == MFK_INVALID_REQUEST && !duplicate);
        CHECK(renderer.submitted_pictures() == 0);
        UINT presents = 0;
        CHECK(SUCCEEDED(swapchain->GetLastPresentCount(&presents)) && presents == 0);
        DXGI_SWAP_CHAIN_DESC1 target_description{};
        CHECK(SUCCEEDED(swapchain->GetDesc1(&target_description)));
        CHECK(target_description.Format == DXGI_FORMAT_R16G16B16A16_FLOAT);
        CHECK(renderer.present(frame, nullptr, 0, 0) == MFK_OK && renderer.submitted_pictures() == 1);
        surface.width = 640; surface.height = 400;
        surface.scale_x = surface.scale_y = 1.5f;
        CHECK(renderer.surface_update(surface) == MFK_OK);
        CHECK(SUCCEEDED(swapchain->GetDesc1(&target_description)));
        CHECK(target_description.Width == 640 && target_description.Height == 400);
        IDXGISwapChain2 *scaled = nullptr;
        CHECK(SUCCEEDED(swapchain->QueryInterface(__uuidof(IDXGISwapChain2),
                                               reinterpret_cast<void **>(&scaled))));
        DXGI_MATRIX_3X2_F matrix{};
        CHECK(SUCCEEDED(scaled->GetMatrixTransform(&matrix)));
        CHECK(near_value(matrix._11, 1.0f / surface.scale_x) &&
              near_value(matrix._22, 1.0f / surface.scale_y));
        release(scaled);
        CHECK(renderer.present(frame, nullptr, 4.0 / 3, 1) == MFK_OK && renderer.submitted_pictures() == 2);
        CHECK(SUCCEEDED(swapchain->GetLastPresentCount(&presents)) && presents == 2);
        surface.width = surface.height = 0;
        CHECK(renderer.surface_update(surface) == MFK_OK);
        CHECK(renderer.present(frame, nullptr, 0, 1) == MFK_OK && renderer.submitted_pictures() == 2);
        CHECK(SUCCEEDED(swapchain->GetLastPresentCount(&presents)) && presents == 2);
        surface.width = 640; surface.height = 400;
        surface.visible = 0; surface.display_window = nullptr;
        CHECK(renderer.surface_update(surface) == MFK_OK);
        CHECK(renderer.present(frame, nullptr, 0, 1) == MFK_OK && renderer.submitted_pictures() == 2);
        CHECK(SUCCEEDED(swapchain->GetLastPresentCount(&presents)) && presents == 2);
        release(swapchain); target = nullptr;
        CHECK(renderer.attach(nullptr, &target) == MFK_OK && !target && renderer.hardware_device() == hardware);
        surface.visible = 1; surface.display_window = window;
        CHECK(renderer.attach(&surface, &target) == MFK_OK && target);
        swapchain = static_cast<IDXGISwapChain1 *>(target);
        CHECK(renderer.present(frame, nullptr, 0, 2) == MFK_OK && renderer.submitted_pictures() == 3);
        std::puts("production D3D11 composition shader/Present, stable resize and detached handoff passed; no UI surface bound");
        // Silent preparation does not decide the user's display. No mode
        // changes or SDR downgrade are performed.
        release(swapchain); target = nullptr;
        CHECK(renderer.attach(nullptr, &target) == MFK_OK && !target);
        frame->color_primaries = AVCOL_PRI_BT2020; frame->color_trc = AVCOL_TRC_SMPTE2084;
        frame->colorspace = AVCOL_SPC_BT2020_NCL;
        CHECK(renderer.inspect(frame, nullptr) == MFK_FORMAT_UNSUPPORTED); // Eight-bit HDR is rejected.
        av_frame_unref(frame);
        frame->format = AV_PIX_FMT_YUV420P10LE; frame->width = 160; frame->height = 90;
        frame->color_primaries = AVCOL_PRI_BT2020; frame->color_trc = AVCOL_TRC_SMPTE2084;
        frame->colorspace = AVCOL_SPC_BT2020_NCL; frame->color_range = AVCOL_RANGE_MPEG;
        CHECK(av_frame_get_buffer(frame, 32) == 0);
        for (int plane = 0; plane < 3; plane++)
            for (int y = 0; y < (plane ? 45 : 90); y++) {
                auto *row = reinterpret_cast<uint16_t *>(frame->data[plane] + y * frame->linesize[plane]);
                std::fill(row, row + (plane ? 80 : 160), plane ? 512 : 600);
            }
        CHECK(renderer.inspect(frame, nullptr) == MFK_OK && renderer.native_hdr());
        const int attached = renderer.attach(&surface, &target);
        if (attached == MFK_HDR_DISPLAY_UNSUPPORTED) {
            CHECK(renderer.submitted_pictures() == 3);
            std::puts("native HDR unsupported display rejected; no SDR fallback or mode switch");
        } else {
            CHECK(attached == MFK_OK);
            CHECK(renderer.present(frame, nullptr, 0, 3) == MFK_OK && renderer.submitted_pictures() == 4);
            std::puts("native HDR swapchain/shader/Present accepted; physical signal not measured");
            swapchain = static_cast<IDXGISwapChain1 *>(target);
            release(swapchain);
            CHECK(renderer.attach(nullptr, &target) == MFK_OK && !target);
        }
        av_frame_free(&frame);
        CHECK(SUCCEEDED(device->GetDeviceRemovedReason()));
    }
    CHECK(DestroyWindow(window));
    CHECK(UnregisterClassW(type.lpszClassName, type.hInstance));
    CoUninitialize();
}
}

int main(int argc, char **argv)
{
    if (argc == 2 && strcmp(argv[1], "--device") == 0) {
        device_rendering();
        return 0;
    }
    CHECK(argc == 1);
    dovi_coefficients();
    dovi_residual_and_rejection();
    missing_dovi_metadata();
    hdr_frame_metadata();
    subtitle_colors_and_text();
    subtitle_overlay_upload();
    std::puts("film renderer unit tests passed (no actual GPU/display validation)");
    return 0;
}
