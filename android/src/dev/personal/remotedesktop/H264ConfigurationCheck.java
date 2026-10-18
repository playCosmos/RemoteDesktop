package dev.personal.remotedesktop;

final class H264ConfigurationCheck {
    private H264ConfigurationCheck() {}

    static int run() {
        int checks=0;
        byte[] spsA={0,0,0,1,0x67,1};
        byte[] ppsA={0,0,0,1,0x68,1};
        byte[] spsB={0,0,0,1,0x67,2};
        byte[] ppsB={0,0,0,1,0x68,2};
        H264Configuration.Pair initial=H264Configuration.merge(null,null,spsA,ppsA);
        require(initial.changed && initial.complete && initial.sps==spsA && initial.pps==ppsA); checks++;
        H264Configuration.Pair repeated=H264Configuration.merge(spsA,ppsA,null,null);
        require(!repeated.changed && repeated.sps==spsA && repeated.pps==ppsA); checks++;
        H264Configuration.Pair ppsOnly=H264Configuration.merge(spsA,ppsA,null,ppsB);
        require(ppsOnly.changed && ppsOnly.sps==spsA && ppsOnly.pps==ppsB); checks++;
        H264Configuration.Pair spsOnly=H264Configuration.merge(spsA,ppsA,spsB,null);
        require(spsOnly.changed && spsOnly.sps==spsB && spsOnly.pps==ppsA); checks++;
        H264Configuration.Pair sameBytes=H264Configuration.merge(spsA,ppsA,spsA.clone(),ppsA.clone());
        require(!sameBytes.changed); checks++;
        H264Configuration.Pair noPair=H264Configuration.merge(null,null,spsA,null);
        require(noPair.sps==spsA && noPair.pps==null && noPair.changed && !noPair.complete); checks++;
        return checks;
    }

    public static void main(String[] args) {
        System.out.println("Android H.264 configuration: " + run() + " checks passed.");
    }

    private static void require(boolean condition) {
        if(!condition) throw new AssertionError("Android H.264 configuration failed");
    }
}
