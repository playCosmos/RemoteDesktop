package dev.personal.remotedesktop;

import android.os.SystemClock;
import org.json.JSONArray;
import org.json.JSONObject;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

/** Owns the authenticated Android session and its connection recovery lifecycle. */
final class RemoteSession {
    interface Listener {
        boolean resumed();
        String preferredDisplay(String host);
        void connected(String host, String fingerprint, String display, int attempt);
        void reconnected(String display, int attempt);
        void status(String message, int holdMs);
        void report(Exception error);
        void hideHealth();
        void inputQueueOverflow();
        int frameRate();
        void requestFrame(RemoteApi api, String display, int requestedFps) throws Exception;
    }

    interface ApiFactory { RemoteApi create(String host, String fingerprint) throws Exception; }

    private final Listener listener;
    private final ApiFactory apiFactory;
    private final ThreadPoolExecutor control = new ThreadPoolExecutor(1, 1, 0, TimeUnit.MILLISECONDS, new LinkedBlockingQueue<Runnable>());
    private final ScheduledExecutorService retryTimer = Executors.newSingleThreadScheduledExecutor();
    private final ScheduledExecutorService frames = Executors.newSingleThreadScheduledExecutor();
    private final Object sessionGate = new Object();
    private final AtomicBoolean recovering = new AtomicBoolean();
    private final AtomicInteger generation = new AtomicInteger();
    private volatile RemoteApi remote;
    private volatile boolean connecting;
    private volatile boolean reconnectNeeded;
    private volatile boolean backgroundUncertain;
    private volatile boolean inputOverflowed;
    private volatile int foregroundFailures;
    private volatile String sessionPassword = "";
    private volatile String sessionHost = "";
    private volatile String sessionFingerprint = "";
    private volatile String selectedDisplay = "";
    private volatile long nextKeepaliveAt;
    private volatile long nextFrameAt;
    private final AtomicBoolean frameLoopStarted = new AtomicBoolean();

    RemoteSession(Listener listener) { this(listener, RemoteApi::new); }

    RemoteSession(Listener listener, ApiFactory apiFactory) {
        this.listener = listener;
        this.apiFactory = apiFactory;
    }

    RemoteApi remote() { return remote; }
    int generation() { return generation.get(); }
    boolean connecting() { return connecting; }
    boolean recovering() { return recovering.get(); }
    boolean reconnectNeeded() { return reconnectNeeded; }
    boolean backgroundUncertain() { return backgroundUncertain; }
    String selectedDisplay() { return selectedDisplay; }
    void selectDisplay(String display) { selectedDisplay = display == null ? "" : display; }
    boolean isCurrent(int attempt, RemoteApi api) {
        synchronized (sessionGate) { return attempt == generation.get() && remote == api; }
    }
    boolean isCurrent(int attempt, RemoteApi api, String display) {
        synchronized (sessionGate) { return attempt == generation.get() && remote == api && selectedDisplay.equals(display); }
    }
    boolean runIfCurrent(int attempt, RemoteApi api, Runnable action) {
        synchronized (sessionGate) {
            if (attempt != generation.get() || remote != api) return false;
            action.run();
            return true;
        }
    }
    boolean runIfCurrent(int attempt, RemoteApi api, String display, Runnable action) {
        synchronized (sessionGate) {
            if (attempt != generation.get() || remote != api || !selectedDisplay.equals(display)) return false;
            action.run();
            return true;
        }
    }

    void connect(String host, String fingerprint, String password) {
        if (remote != null || connecting || recovering.get()) return;
        connecting = true;
        listener.status("접속 중…", 2400);
        final int attempt = generation.get();
        try {
            control.execute(() -> {
                RemoteApi api = null;
                try {
                    api = apiFactory.create(host, fingerprint);
                    api.login(password);
                    JSONArray list = new JSONArray(new String(api.request("displays", "GET", null), "UTF-8"));
                    String display = defaultDisplay(list, listener.preferredDisplay(host));
                    boolean accepted = false;
                    synchronized (sessionGate) {
                        if (attempt == generation.get()) {
                            selectedDisplay = display;
                            sessionHost = host;
                            sessionFingerprint = fingerprint;
                            sessionPassword = password;
                            inputOverflowed = false;
                            reconnectNeeded = false;
                            backgroundUncertain = false;
                            foregroundFailures = 0;
                            nextKeepaliveAt = SystemClock.elapsedRealtime() + RemoteSessionPolicy.KEEPALIVE_INTERVAL_MS;
                            remote = api;
                            api = null;
                            accepted = true;
                        }
                    }
                    if (!accepted) { try { api.logout(); } catch (Exception ignored) {} return; }
                    listener.connected(host, fingerprint, display, attempt);
                } catch (Exception error) {
                    if (api != null) try { api.logout(); } catch (Exception ignored) {}
                    listener.report(error);
                } finally { connecting = false; }
            });
        } catch (RuntimeException rejected) {
            connecting = false;
            listener.report(rejected);
        }
    }

    RemoteApi disconnect() {
        RemoteApi api;
        synchronized (sessionGate) {
            generation.incrementAndGet();
            api = remote;
            remote = null;
            sessionPassword = "";
            sessionHost = "";
            sessionFingerprint = "";
            reconnectNeeded = false;
            backgroundUncertain = false;
            foregroundFailures = 0;
            selectedDisplay = "";
        }
        if (api != null) {
            api.cancelled = true;
            if (!control.isShutdown()) {
                try { control.execute(api::logout); }
                catch (RuntimeException ignored) {}
            }
        }
        return api;
    }

    void send(JSONObject event, boolean inputEnabled) {
        RemoteApi api = remote;
        if (!RemoteSessionPolicy.acceptsInput(api != null, inputOverflowed, inputEnabled, "release".equals(event.optString("kind"))) || control.isShutdown()) return;
        if (control.getQueue().size() > 128) {
            inputOverflowed = true;
            listener.inputQueueOverflow();
            return;
        }
        try {
            control.execute(() -> {
                if (remote != api) return;
                try {
                    String notice = api.input(event);
                    if (notice != null) listener.status(notice, 2400);
                } catch (Exception error) { noteSessionFailure(api, error); }
            });
        } catch (RuntimeException rejected) { if (remote == api) listener.report(rejected); }
    }

    void keepalive() {
        RemoteApi api = remote;
        if (api == null || !RemoteSessionPolicy.shouldKeepalive(recovering.get(), reconnectNeeded)) return;
        long now = SystemClock.elapsedRealtime();
        if (now < nextKeepaliveAt) return;
        nextKeepaliveAt = now + RemoteSessionPolicy.KEEPALIVE_INTERVAL_MS;
        try {
            api.ping();
            if (remote != api) return;
            backgroundUncertain = false;
            foregroundFailures = 0;
        } catch (Exception error) {
            if (remote != api) return;
            backgroundUncertain = true;
            if (statusCode(error) == 401) { reconnectNeeded = true; recover(api, true); }
        }
    }

    void noteFrameReceived() { foregroundFailures = 0; }

    void requestImmediateFrame() { nextFrameAt = 0; }

    void startFrameLoop() {
        if (!frameLoopStarted.compareAndSet(false, true)) return;
        frames.scheduleWithFixedDelay(this::frameTick, 0, 10, TimeUnit.MILLISECONDS);
    }

    private void frameTick() {
        RemoteApi api = remote;
        try {
            if (api == null) return;
            if (!listener.resumed()) { keepalive(); return; }
            if (reconnectNeeded || recovering.get()) return;
            int rate = listener.frameRate();
            long now = SystemClock.elapsedRealtime();
            if (!RemoteSessionPolicy.frameDue(now, nextFrameAt)) return;
            nextFrameAt = RemoteSessionPolicy.nextFrameAt(now, rate);
            listener.requestFrame(api, selectedDisplay, rate);
        } catch (Exception error) {
            if (api != null && remote == api) noteSessionFailure(api, error);
        }
    }

    void noteSessionFailure(RemoteApi api, Exception error) {
        RemoteSessionPolicy.FrameFailure action = RemoteSessionPolicy.frameFailure(remote == api, statusCode(error), listener.resumed(), foregroundFailures);
        if (action == RemoteSessionPolicy.FrameFailure.IGNORE || action == RemoteSessionPolicy.FrameFailure.RETRY_FRAME) {
            if (action == RemoteSessionPolicy.FrameFailure.RETRY_FRAME) foregroundFailures++;
            return;
        }
        if (action == RemoteSessionPolicy.FrameFailure.DEFER_UNTIL_RESUME) { backgroundUncertain = true; return; }
        reconnectNeeded = true;
        recover(api, true);
    }

    void onStart() {
        foregroundFailures = 0;
        requestImmediateFrame();
        RemoteApi api = remote;
        if (api == null || (!reconnectNeeded && !backgroundUncertain)) return;
        listener.status("다시 연결 중", -1);
        listener.hideHealth();
        recover(api, reconnectNeeded);
    }

    void onStop() { nextKeepaliveAt = 0; }

    private void recover(RemoteApi failed, boolean forceLogin) {
        if (failed == null || sessionPassword.isEmpty() || !recovering.compareAndSet(false, true)) return;
        final int attempt = generation.get();
        if (forceLogin) {
            listener.status("다시 연결 중", -1);
            listener.hideHealth();
        }
        try {
            control.execute(() -> {
                RemoteApi fresh = null;
                try {
                    if (!sameSession(attempt, failed)) return;
                    if (!forceLogin) {
                        try {
                            failed.ping();
                            if (!sameSession(attempt, failed)) return;
                            backgroundUncertain = false;
                            reconnectNeeded = false;
                            foregroundFailures = 0;
                            listener.status("연결됨", 1200);
                            return;
                        } catch (Exception error) {
                            if (!sameSession(attempt, failed)) return;
                            if (statusCode(error) != 401 && !listener.resumed()) { backgroundUncertain = true; return; }
                            reconnectNeeded = true;
                            listener.status("다시 연결 중", -1);
                            listener.hideHealth();
                        }
                    }
                    String host = sessionHost, fingerprint = sessionFingerprint, password = sessionPassword;
                    if (password.isEmpty() || !sameSession(attempt, failed)) return;
                    fresh = apiFactory.create(host, fingerprint);
                    fresh.login(password);
                    JSONArray list = new JSONArray(new String(fresh.request("displays", "GET", null), "UTF-8"));
                    String display = defaultDisplay(list, selectedDisplay);
                    boolean accepted = false;
                    RemoteApi previous = null;
                    synchronized (sessionGate) {
                        if (attempt == generation.get()) {
                            previous = remote;
                            selectedDisplay = display;
                            sessionHost = host;
                            sessionFingerprint = fingerprint;
                            sessionPassword = password;
                            remote = fresh;
                            reconnectNeeded = false;
                            backgroundUncertain = false;
                            foregroundFailures = 0;
                            nextKeepaliveAt = SystemClock.elapsedRealtime() + RemoteSessionPolicy.KEEPALIVE_INTERVAL_MS;
                            fresh = null;
                            accepted = true;
                        }
                    }
                    if (!accepted) { fresh.logout(); return; }
                    listener.reconnected(display, attempt);
                    listener.status("다시 연결됨", 1400);
                    if (previous != null && previous != remote) { previous.cancelled = true; try { previous.logout(); } catch (Exception ignored) {} }
                } catch (Exception error) {
                    if (fresh != null) try { fresh.logout(); } catch (Exception ignored) {}
                    if (!sameSession(attempt, failed)) return;
                    if (RemoteSessionPolicy.busySessionStatus(statusCode(error))) {
                        try {
                            failed.ping();
                            reconnectNeeded = false;
                            backgroundUncertain = false;
                            foregroundFailures = 0;
                            listener.status("연결됨", 1200);
                            return;
                        } catch (Exception pingError) {
                            if (statusCode(pingError) != 401) error = pingError;
                        }
                    }
                    reconnectNeeded = true;
                    backgroundUncertain = true;
                    listener.report(error);
                    retryTimer.schedule(() -> {
                        if (generation.get() == attempt && remote == failed && !sessionPassword.isEmpty()) recover(failed, true);
                    }, RemoteSessionPolicy.recoveryDelayMs(listener.resumed()), TimeUnit.MILLISECONDS);
                } finally { recovering.set(false); }
            });
        } catch (RuntimeException rejected) {
            recovering.set(false);
            if (remote == failed) listener.report(rejected);
        }
    }

    private boolean sameSession(int attempt, RemoteApi api) {
        synchronized (sessionGate) { return attempt == generation.get() && remote == api; }
    }

    private static int statusCode(Exception error) { return error instanceof RemoteApi.StatusException ? ((RemoteApi.StatusException)error).code : 0; }

    static String defaultDisplay(JSONArray list, String wanted) throws Exception {
        String primary = "all";
        for (int i = 0; i < list.length(); i++) {
            JSONObject display = list.getJSONObject(i);
            if (display.getString("id").equals(wanted)) return wanted;
            if (display.getBoolean("primary")) primary = display.getString("id");
        }
        return primary;
    }

    void shutdown() {
        frames.shutdownNow();
        retryTimer.shutdownNow();
        control.shutdown();
    }
}
