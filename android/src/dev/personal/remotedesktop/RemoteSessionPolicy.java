package dev.personal.remotedesktop;

/** Platform-independent decisions for the Android remote-session lifecycle. */
final class RemoteSessionPolicy {
    enum FrameFailure { IGNORE, DEFER_UNTIL_RESUME, RETRY_FRAME, REAUTHENTICATE }

    static final long KEEPALIVE_INTERVAL_MS = 5000L;

    private RemoteSessionPolicy() {}

    static boolean shouldKeepalive(boolean recovering, boolean reconnectNeeded) {
        return !recovering && !reconnectNeeded;
    }

    static boolean acceptsInput(boolean connected, boolean overloaded, boolean inputEnabled, boolean release) {
        return connected && !overloaded && (inputEnabled || release);
    }

    static FrameFailure frameFailure(boolean currentSession, int statusCode, boolean resumed, int failuresBeforeThisFrame) {
        if (!currentSession) return FrameFailure.IGNORE;
        if (statusCode == 401) return FrameFailure.REAUTHENTICATE;
        if (!resumed) return FrameFailure.DEFER_UNTIL_RESUME;
        return failuresBeforeThisFrame + 1 >= 3 ? FrameFailure.REAUTHENTICATE : FrameFailure.RETRY_FRAME;
    }

    static long recoveryDelayMs(boolean resumed) { return resumed ? 3000L : 5000L; }

    static boolean busySessionStatus(int statusCode) { return statusCode == 409; }
}
