#ifndef AVFORMAT_SHIM_H
#define AVFORMAT_SHIM_H

#include <libavformat/avformat.h>
#include <libavformat/avio.h>
#include <libavutil/dovi_meta.h>

//#import <Libavformat/vpcc.h>

/**
 * Writes VP codec configuration to the provided AVIOContext.
 *
 * @param s address of the AVFormatContext for the logging context.
 * @param pb address of the AVIOContext where the vpcC shall be written.
 * @param data address of a data array which contains coded bitstream data from
 *             which codec information can be extracted. May be NULL.
 * @param len length of the data array.
 * @param par address of the AVCodecParameters which contains codec information.
 * @return >=0 in case of success, a negative value corresponding to an AVERROR
 *         code in case of failure
 */
int ff_isom_write_vpcc(AVFormatContext *s, AVIOContext *pb,
                       const uint8_t *data, int len,
                       AVCodecParameters *par);
//int ff_isom_write_avcc(AVIOContext *pb, const uint8_t *data, int len);
/* Internal entry points from the locked FFmpeg 8.1.2 source, not a public ABI. */
void ff_isom_put_dvcc_dvvc(void *logctx, uint8_t out[24],
                         const AVDOVIDecoderConfigurationRecord *dovi);
//int ff_isom_write_av1c(AVIOContext *pb, const uint8_t *buf, int size, int write_seq_header);

#endif /* AVFORMAT_SHIM_H */
