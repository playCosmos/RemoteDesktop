package dev.personal.remotedesktop;

import java.util.Arrays;

final class H264Configuration {
    private H264Configuration() {}

    static final class Pair {
        final byte[] sps;
        final byte[] pps;
        final boolean changed;
        final boolean complete;
        Pair(byte[] sps, byte[] pps, boolean changed) {
            this.sps=sps;
            this.pps=pps;
            this.changed=changed;
            this.complete=sps!=null && pps!=null;
        }
    }

    static Pair merge(byte[] currentSps, byte[] currentPps, byte[] nextSps, byte[] nextPps) {
        byte[] sps=nextSps==null?currentSps:nextSps;
        byte[] pps=nextPps==null?currentPps:nextPps;
        return new Pair(sps,pps,!Arrays.equals(sps,currentSps) || !Arrays.equals(pps,currentPps));
    }
}
