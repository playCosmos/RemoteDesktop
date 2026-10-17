package dev.personal.remotedesktop;

final class SessionBack {
    private SessionBack() {}

    static boolean ask(boolean controlsOpen, boolean connected) { return !controlsOpen && connected; }

    static String prompt(String address) {
        if (address == null) return "원격 화면을 보고 있습니다. 연결을 끊을까요?";
        String remote = address.trim();
        if (remote.isEmpty()) return "원격 화면을 보고 있습니다. 연결을 끊을까요?";
        return remote + " 화면을 보고 있습니다. 연결을 끊을까요?";
    }
}
