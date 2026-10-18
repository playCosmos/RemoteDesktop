package dev.personal.remotedesktop;

final class H264PictureCheck {
    private H264PictureCheck() {}

    public static void main(String[] args) {
        int[] size=H264Picture.pictureSize(sps(119,67,4));
        require(size[0]==1920 && size[1]==1080 && size[2]==1920 && size[3]==1088);
        reject(sps(511,511,0));
        reject(extremeExpGolomb());
        System.out.println("Android H.264 picture limits: 3 checks passed.");
    }

    private static void reject(byte[] sps) {
        try { H264Picture.pictureSize(sps); }
        catch(IllegalArgumentException expected) { return; }
        throw new AssertionError("Oversized or overflowing SPS was accepted");
    }

    private static byte[] sps(int widthMbsMinus1,int heightMapUnitsMinus1,int cropBottom) {
        Writer w=new Writer();
        w.u(66,8); w.u(0,8); w.u(30,8);
        w.ue(0); w.ue(0); w.ue(0); w.ue(0); w.ue(0); w.bit(0);
        w.ue(widthMbsMinus1); w.ue(heightMapUnitsMinus1);
        w.bit(1); w.bit(1); w.bit(cropBottom>0?1:0);
        if(cropBottom>0) { w.ue(0); w.ue(0); w.ue(0); w.ue(cropBottom); }
        w.bit(0);
        byte[] rbsp=w.finish();
        byte[] nal=new byte[rbsp.length+5];
        nal[3]=1; nal[4]=0x67;
        System.arraycopy(rbsp,0,nal,5,rbsp.length);
        return nal;
    }

    private static byte[] extremeExpGolomb() {
        Writer w=new Writer();
        w.u(66,8); w.u(0,8); w.u(30,8);
        for(int i=0;i<31;i++) w.bit(0);
        w.bit(1);
        for(int i=0;i<31;i++) w.bit(0);
        byte[] rbsp=w.finish();
        byte[] nal=new byte[rbsp.length+5];
        nal[3]=1; nal[4]=0x67;
        System.arraycopy(rbsp,0,nal,5,rbsp.length);
        return nal;
    }

    private static void require(boolean condition) {
        if(!condition) throw new AssertionError("Valid H.264 SPS was rejected");
    }

    private static final class Writer {
        private final StringBuilder bits=new StringBuilder();
        void bit(int value) { bits.append(value==0?'0':'1'); }
        void u(int value,int count) { for(int i=count-1;i>=0;i--) bit((value>>>i)&1); }
        void ue(int value) {
            int code=value+1;
            int zeros=31-Integer.numberOfLeadingZeros(code);
            for(int i=0;i<zeros;i++) bit(0);
            u(code,zeros+1);
        }
        byte[] finish() {
            bit(1);
            while((bits.length()&7)!=0) bit(0);
            byte[] output=new byte[bits.length()/8];
            for(int i=0;i<bits.length();i++) if(bits.charAt(i)=='1') output[i/8]|=(byte)(1<<(7-(i&7)));
            return output;
        }
    }
}
