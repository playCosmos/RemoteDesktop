package dev.personal.remotedesktop;

final class UploadResume {
    private UploadResume() {}

    static long nextOffset(long current, long reported, long size) {
        if(current<0 || reported<current || size<0 || current>size || reported>size) return -1;
        return reported;
    }

    static boolean complete(long offset, long size) { return size>=0 && offset==size; }
}
