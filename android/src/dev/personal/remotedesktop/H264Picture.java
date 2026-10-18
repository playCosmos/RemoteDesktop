package dev.personal.remotedesktop;

import android.graphics.Bitmap;
import android.graphics.Rect;
import android.media.Image;
import android.media.MediaCodec;
import android.media.MediaFormat;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.util.Arrays;

final class H264Picture {
    private MediaCodec codec;
    private boolean started;
    private byte[] configuredSps;
    private byte[] configuredPps;
    private int[] configuredSize;
    private long timestampUs;
    private int produced;
    private int[] pixels;

    Bitmap decode(byte[] accessUnit, int fps) throws IOException {
        if (accessUnit == null || accessUnit.length < 4) throw new IOException("H.264 프레임이 비어 있습니다.");
        long deadline=System.nanoTime()+H264Configuration.decodeBudgetNs(fps,produced==0);
        byte[] sps = nal(accessUnit, 7);
        byte[] pps = nal(accessUnit, 8);
        try {
            H264Configuration.Pair configuration=H264Configuration.merge(configuredSps,configuredPps,sps,pps);
            if(configuration.complete && configuration.changed) configure(configuration.sps,configuration.pps);
            if (codec == null || !started) throw new IOException("H.264 구성 정보가 없습니다.");
            long remainingNs=deadline-System.nanoTime();
            if(remainingNs<=0) throw new IOException("H.264 입력 대기 시간 초과");
            long remainingUs=Math.max(1L,(remainingNs+999L)/1000L);
            int index = codec.dequeueInputBuffer((int)Math.min(15_000L,remainingUs));
            if (index < 0) throw new IOException("H.264 입력 버퍼가 없습니다.");
            ByteBuffer input = codec.getInputBuffer(index);
            if (input == null || input.capacity() < accessUnit.length) throw new IOException("H.264 입력 크기 초과");
            input.clear();
            input.put(accessUnit);
            int flags = nal(accessUnit, 5) != null ? MediaCodec.BUFFER_FLAG_KEY_FRAME : 0;
            codec.queueInputBuffer(index, 0, accessUnit.length, timestampUs, flags);
            timestampUs += H264Configuration.frameDurationUs(fps);
            Bitmap bitmap = pull(deadline);
            produced++;
            return bitmap;
        } catch (IllegalStateException error) {
            releaseCodec();
            throw new IOException("H.264 디코더 상태 오류");
        }
    }

    void close() { releaseCodec(); }

    private void configure(byte[] sps, byte[] pps) throws IOException {
        int[] size;
        try { size = pictureSize(sps); }
        catch (IllegalArgumentException error) { throw new IOException("H.264 구성 정보를 읽지 못했습니다."); }
        releaseCodec();
        MediaCodec created = MediaCodec.createDecoderByType("video/avc");
        try {
            try {
                created.configure(format(sps, pps, size, true), null, null, 0);
            } catch (IllegalArgumentException error) {
                created.release();
                created = MediaCodec.createDecoderByType("video/avc");
                created.configure(format(sps, pps, size, false), null, null, 0);
            }
            created.start();
            codec = created;
            started = true;
            configuredSps = Arrays.copyOf(sps, sps.length);
            configuredPps = Arrays.copyOf(pps, pps.length);
            configuredSize = size;
            produced = 0;
            created = null;
        } finally {
            if (created != null) {
                try { created.release(); } catch (Exception ignored) {}
            }
        }
    }

    private static MediaFormat format(byte[] sps, byte[] pps, int[] size, boolean lowLatency) {
        MediaFormat format = MediaFormat.createVideoFormat("video/avc", size[2], size[3]);
        format.setByteBuffer("csd-0", ByteBuffer.wrap(sps));
        format.setByteBuffer("csd-1", ByteBuffer.wrap(pps));
        format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 8 * 1024 * 1024);
        if (lowLatency) format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1);
        return format;
    }

    private Bitmap pull(long deadlineNs) throws IOException {
        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        while (System.nanoTime() < deadlineNs) {
            long remainingNs=deadlineNs-System.nanoTime();
            long remainingUs=remainingNs/1000L;
            if(remainingUs<=0) break;
            int out = codec.dequeueOutputBuffer(info, Math.min(20_000L,remainingUs));
            if (out == MediaCodec.INFO_TRY_AGAIN_LATER || out == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) continue;
            if (out < 0) continue;
            if ((info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0 || info.size == 0) {
                codec.releaseOutputBuffer(out, false);
                continue;
            }
            Image image = null;
            try {
                image = codec.getOutputImage(out);
                if (image == null) throw new IOException("H.264 출력 이미지가 없습니다.");
                return toBitmap(image);
            } finally {
                if (image != null) image.close();
                codec.releaseOutputBuffer(out, false);
            }
        }
        throw new IOException("H.264 출력 시간 초과");
    }

    private Bitmap toBitmap(Image image) throws IOException {
        Rect crop = image.getCropRect();
        int width = crop.width();
        int height = crop.height();
        int originX = crop.left;
        int originY = crop.top;
        if (configuredSize != null && width == configuredSize[2] && height == configuredSize[3] && (configuredSize[0] != width || configuredSize[1] != height)) {
            originX = configuredSize[4];
            originY = configuredSize[5];
            width = configuredSize[0];
            height = configuredSize[1];
        }
        if (width <= 1 || height <= 1) throw new IOException("H.264 출력 크기 오류");
        Image.Plane[] planes = image.getPlanes();
        if (planes == null || planes.length < 3) throw new IOException("H.264 출력 형식 오류");
        Image.Plane yPlane = planes[0];
        Image.Plane uPlane = planes[1];
        Image.Plane vPlane = planes[2];
        if (yPlane.getRowStride() <= 0 || uPlane.getRowStride() <= 0 || vPlane.getRowStride() <= 0 || yPlane.getPixelStride() <= 0 || uPlane.getPixelStride() <= 0 || vPlane.getPixelStride() <= 0) throw new IOException("H.264 출력 형식 오류");
        ByteBuffer yBuffer = yPlane.getBuffer();
        ByteBuffer uBuffer = uPlane.getBuffer();
        ByteBuffer vBuffer = vPlane.getBuffer();
        int yBase = yBuffer.position();
        int uBase = uBuffer.position();
        int vBase = vBuffer.position();
        int yRow = yPlane.getRowStride();
        int uRow = uPlane.getRowStride();
        int vRow = vPlane.getRowStride();
        int yPixel = yPlane.getPixelStride();
        int uPixel = uPlane.getPixelStride();
        int vPixel = vPlane.getPixelStride();
        int count = width * height;
        if (pixels == null || pixels.length != count) pixels = new int[count];
        try {
            for (int row = 0; row < height; row++) {
                int yIndex = yBase + (originY + row) * yRow + originX * yPixel;
                int uIndex = uBase + ((originY + row) / 2) * uRow + (originX / 2) * uPixel;
                int vIndex = vBase + ((originY + row) / 2) * vRow + (originX / 2) * vPixel;
                int offset = row * width;
                for (int col = 0; col < width; col++) {
                    int y = yBuffer.get(yIndex) & 255;
                    int u = uBuffer.get(uIndex + (col / 2) * uPixel) & 255;
                    int v = vBuffer.get(vIndex + (col / 2) * vPixel) & 255;
                    int c = y - 16;
                    int d = u - 128;
                    int e = v - 128;
                    int red = clamp((298 * c + 409 * e + 128) >> 8);
                    int green = clamp((298 * c - 100 * d - 208 * e + 128) >> 8);
                    int blue = clamp((298 * c + 516 * d + 128) >> 8);
                    pixels[offset + col] = (255 << 24) | (red << 16) | (green << 8) | blue;
                    yIndex += yPixel;
                }
            }
        } catch (IndexOutOfBoundsException error) {
            throw new IOException("H.264 출력 형식 오류");
        }
        return Bitmap.createBitmap(pixels, width, height, Bitmap.Config.ARGB_8888);
    }

    private void releaseCodec() {
        MediaCodec current = codec;
        codec = null;
        started = false;
        configuredSps = null;
        configuredPps = null;
        configuredSize = null;
        produced = 0;
        if (current == null) return;
        try { current.stop(); } catch (Exception ignored) {}
        try { current.release(); } catch (Exception ignored) {}
    }

    private static int clamp(int value) { return value < 0 ? 0 : Math.min(255, value); }

    static int[] pictureSize(byte[] nalWithStart) {
        byte[] rbsp = rbsp(nalWithStart);
        if (rbsp.length < 4) throw new IllegalArgumentException("sps");
        int profile = rbsp[0] & 255;
        Bits bits = new Bits(rbsp, 3);
        bits.ue();
        int chroma = 1;
        boolean separate = false;
        if (high(profile)) {
            chroma = bits.ue();
            if (chroma == 3) separate = bits.u(1) == 1;
            bits.ue();
            bits.ue();
            bits.u(1);
            if (bits.u(1) == 1) {
                int lists = chroma == 3 ? 12 : 8;
                for (int i = 0; i < lists; i++) if (bits.u(1) == 1) skipScaling(bits, i < 6 ? 16 : 64);
            }
        }
        bits.ue();
        int order = bits.ue();
        if (order == 0) bits.ue();
        else if (order == 1) {
            bits.u(1);
            bits.se();
            bits.se();
            int cycle = bits.ue();
            if (cycle > 255) throw new IllegalArgumentException("sps");
            for (int i = 0; i < cycle; i++) bits.se();
        } else if (order != 2) throw new IllegalArgumentException("sps");
        bits.ue();
        bits.u(1);
        int codedWidth = (bits.ue() + 1) * 16;
        int mapUnits = bits.ue() + 1;
        int frameOnly = bits.u(1);
        if (frameOnly == 0) bits.u(1);
        bits.u(1);
        int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
        if (bits.u(1) == 1) {
            cropLeft = bits.ue();
            cropRight = bits.ue();
            cropTop = bits.ue();
            cropBottom = bits.ue();
        }
        int arrayType = separate ? 0 : chroma;
        int cropX;
        int cropY;
        if (arrayType == 0) { cropX = 1; cropY = 2 - frameOnly; }
        else if (arrayType == 1) { cropX = 2; cropY = 2 * (2 - frameOnly); }
        else if (arrayType == 2) { cropX = 2; cropY = 2 - frameOnly; }
        else { cropX = 1; cropY = 2 - frameOnly; }
        int codedHeight = mapUnits * 16 * (2 - frameOnly);
        int visibleWidth = codedWidth - (cropLeft + cropRight) * cropX;
        int visibleHeight = codedHeight - (cropTop + cropBottom) * cropY;
        if (visibleWidth <= 1 || visibleHeight <= 1 || codedWidth > 8192 || codedHeight > 8192) throw new IllegalArgumentException("sps");
        return new int[] { visibleWidth, visibleHeight, codedWidth, codedHeight, cropLeft * cropX, cropTop * cropY };
    }

    private static boolean high(int profile) {
        return profile == 100 || profile == 110 || profile == 122 || profile == 244 || profile == 44 || profile == 83 || profile == 86 || profile == 118 || profile == 128 || profile == 138 || profile == 139 || profile == 134 || profile == 135;
    }

    private static void skipScaling(Bits bits, int size) {
        int last = 8, next = 8;
        for (int i = 0; i < size; i++) {
            if (next != 0) next = Math.floorMod(last + bits.se(), 256);
            last = next == 0 ? last : next;
        }
    }

    private static byte[] rbsp(byte[] nalWithStart) {
        int header = startLength(nalWithStart, 0);
        if (header == 0 || header >= nalWithStart.length) throw new IllegalArgumentException("sps");
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        int zeros = 0;
        for (int i = header + 1; i < nalWithStart.length; i++) {
            int value = nalWithStart[i] & 255;
            if (zeros == 2 && value == 3) { zeros = 0; continue; }
            out.write(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return out.toByteArray();
    }

    static byte[] nal(byte[] data, int type) {
        int i = 0;
        while (i + 3 <= data.length) {
            int marker = startLength(data, i);
            if (marker == 0) { i++; continue; }
            int header = i + marker;
            if (header >= data.length) return null;
            int end = header + 1;
            while (end < data.length && startLength(data, end) == 0) end++;
            if ((data[header] & 31) == type) return Arrays.copyOfRange(data, i, end);
            i = end;
        }
        return null;
    }

    private static int startLength(byte[] data, int i) {
        if (i + 3 <= data.length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) return 3;
        if (i + 4 <= data.length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1) return 4;
        return 0;
    }

    private static final class Bits {
        private final byte[] data;
        private int bit;
        Bits(byte[] data, int byteOffset) { this.data = data; this.bit = byteOffset * 8; }
        int u(int count) {
            int value = 0;
            for (int i = 0; i < count; i++) value = (value << 1) | one();
            return value;
        }
        int ue() {
            int zeros = 0;
            while (one() == 0) {
                zeros++;
                if (zeros > 31) throw new IllegalArgumentException("sps");
            }
            return zeros == 0 ? 0 : ((1 << zeros) - 1) + u(zeros);
        }
        int se() {
            int code = ue();
            int magnitude = (code + 1) >> 1;
            return (code & 1) == 0 ? -magnitude : magnitude;
        }
        private int one() {
            int index = bit >> 3;
            if (index >= data.length) throw new IllegalArgumentException("sps");
            int shift = 7 - (bit & 7);
            bit++;
            return ((data[index] & 255) >> shift) & 1;
        }
    }
}
