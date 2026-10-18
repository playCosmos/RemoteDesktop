package dev.personal.remotedesktop;

final class VideoFallback {
    static final int LIMIT=3;
    private VideoFallback() {}

    static int note(int streak, boolean hasPicture, boolean requestedH264, boolean receivedH264) {
        streak=Math.max(0,Math.min(streak,LIMIT));
        if(!hasPicture || !requestedH264) return streak;
        return receivedH264?0:Math.min(LIMIT,streak+1);
    }

    static boolean shouldUseJpeg(int streak) { return streak>=LIMIT; }
}
