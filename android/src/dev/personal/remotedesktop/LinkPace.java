package dev.personal.remotedesktop;

final class LinkPace {
    private LinkPace() {}

    static int[] pace(int ceiling, int current, double transferMs, int fps, int slow, int fast, boolean picture) {
        ceiling = clamp(ceiling);
        current = Math.max(25, Math.min(current, ceiling));
        if (!picture || transferMs < 0 || Double.isNaN(transferMs) || Double.isInfinite(transferMs)) return new int[]{current, slow, fast, 0};
        int rate = fps == 5 || fps == 10 || fps == 15 || fps == 30 ? fps : 15;
        double budget = 1000.0 / rate;
        if (transferMs > budget * 1.25) { slow++; fast = 0; }
        else if (transferMs <= budget * 0.5) { fast++; slow = 0; }
        else { slow = 0; fast = 0; }
        if (slow >= 3 && current > 25) {
            int next = lower(current);
            if (next < current) return new int[]{next, 0, 0, 1};
        }
        if (fast >= 8 && current < ceiling) {
            int next = raise(current, ceiling);
            if (next > current) return new int[]{next, 0, 0, 1};
        }
        return new int[]{current, slow, fast, 0};
    }

    static String notice(int ceiling, int previous, int next) {
        if (previous == next) return null;
        if (next < previous) {
            String from = name(ceiling);
            String to = name(next);
            if (from.equals(to)) return "화면이 밀려 화질을 더 낮췄습니다.";
            return "화면이 밀려 화질을 낮췄습니다. " + from + " 대신 " + to + ".";
        }
        if (next >= ceiling) return "화면에 여유가 생겨 화질을 " + euro(name(ceiling)) + " 되돌렸습니다.";
        String raised = name(next);
        if (raised.equals(name(previous))) return "화면에 여유가 생겨 화질을 조금 올렸습니다.";
        return "화면에 여유가 생겨 화질을 " + euro(raised) + " 올렸습니다.";
    }

    private static int lower(int quality) {
        if (quality >= 70) return 55;
        if (quality >= 50) return 40;
        return 25;
    }

    private static int raise(int quality, int ceiling) {
        int next = quality < 40 ? 40 : quality < 55 ? 55 : quality < 75 ? 75 : 90;
        return Math.min(ceiling, next);
    }

    private static int clamp(int quality) { return Math.max(25, Math.min(90, quality)); }

    private static String name(int quality) {
        int level = clamp(quality);
        return level <= 40 ? "저대역폭" : level >= 70 ? "고화질" : "균형";
    }

    private static String euro(String word) {
        if (word.isEmpty()) return "로";
        char last = word.charAt(word.length() - 1);
        if (last < '가' || last > '힣') return word + "로";
        int batchim = (last - '가') % 28;
        return word + (batchim == 0 || batchim == 8 ? "로" : "으로");
    }
}
