package dev.personal.remotedesktop;

public final class RemoteSessionPolicyCheck {
    private static int checks;

    private static void check(boolean condition, String name) {
        if (!condition) throw new AssertionError(name);
        checks++;
    }

    public static void main(String[] args) {
        check(RemoteSessionPolicy.KEEPALIVE_INTERVAL_MS == 5000L, "background ping keeps the five-second interval");
        check(RemoteSessionPolicy.shouldKeepalive(false, false), "a healthy idle session sends keepalive");
        check(!RemoteSessionPolicy.shouldKeepalive(true, false) && !RemoteSessionPolicy.shouldKeepalive(false, true), "keepalive pauses during recovery");
        check(RemoteSessionPolicy.acceptsInput(true, false, true, false) && RemoteSessionPolicy.acceptsInput(true, false, false, true), "input disable still permits a release event");
        check(!RemoteSessionPolicy.acceptsInput(false, false, true, false) && !RemoteSessionPolicy.acceptsInput(true, true, true, false), "disconnected or overloaded sessions reject further input");
        check(RemoteSessionPolicy.frameFailure(false, 0, true, 0) == RemoteSessionPolicy.FrameFailure.IGNORE, "a replaced session ignores its late frame failure");
        check(RemoteSessionPolicy.frameFailure(true, 401, false, 0) == RemoteSessionPolicy.FrameFailure.REAUTHENTICATE, "expired authentication recovers immediately in the background");
        check(RemoteSessionPolicy.frameFailure(true, 0, false, 0) == RemoteSessionPolicy.FrameFailure.DEFER_UNTIL_RESUME, "a background network failure waits for resume");
        check(RemoteSessionPolicy.frameFailure(true, 0, true, 0) == RemoteSessionPolicy.FrameFailure.RETRY_FRAME && RemoteSessionPolicy.frameFailure(true, 0, true, 1) == RemoteSessionPolicy.FrameFailure.RETRY_FRAME, "foreground frame failures tolerate two misses");
        check(RemoteSessionPolicy.frameFailure(true, 0, true, 2) == RemoteSessionPolicy.FrameFailure.REAUTHENTICATE, "three consecutive foreground misses start recovery");
        check(RemoteSessionPolicy.recoveryDelayMs(true) == 3000L && RemoteSessionPolicy.recoveryDelayMs(false) == 5000L, "recovery retries respect foreground and background delays");
        check(RemoteSessionPolicy.busySessionStatus(409) && !RemoteSessionPolicy.busySessionStatus(401), "a busy-host response probes the existing token before replacing it");
        System.out.println("Android remote-session policy: " + checks + " checks passed.");
    }
}
