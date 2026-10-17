package dev.personal.remotedesktop;

final class SecureNotice {
    private SecureNotice() {}

    static String state(String header) {
        if (header == null) return "off";
        String value = header.trim();
        if ("live".equals(value) || "hidden".equals(value) || "off".equals(value)) return value;
        return "off";
    }

    static String text(String header) {
        String value = state(header);
        if ("live".equals(value)) return "보안 화면 · 클릭과 키는 이 화면으로 갑니다.";
        if ("hidden".equals(value)) return "상대 PC에 보안 화면이 떠 있습니다. 모든 사용자로 설치되어 있어야 그 화면이 보입니다.";
        return null;
    }

    static boolean visible(boolean controlsOpen, String notice) {
        return notice != null && !notice.isEmpty() && !controlsOpen;
    }
}
