package dev.personal.remotedesktop;

final class UploadResumeCheck {
    private UploadResumeCheck() {}

    static int run() {
        int checks=0;
        long size=1000, half=400;
        long resumed=UploadResume.nextOffset(0,half,size);
        require(resumed==half); checks++;
        require(UploadResume.complete(resumed,size)==false); checks++;
        require(UploadResume.nextOffset(resumed,resumed,size)==resumed); checks++;
        require(UploadResume.nextOffset(0,0,size)==0); checks++;
        require(UploadResume.nextOffset(resumed,resumed-1,size)==-1); checks++;
        require(UploadResume.nextOffset(resumed,size+1,size)==-1); checks++;
        require(UploadResume.nextOffset(-1,half,size)==-1); checks++;
        require(UploadResume.complete(size,size)); checks++;
        require(UploadResume.complete(0,0)); checks++;
        require(!UploadResume.complete(-1,0)); checks++;
        return checks;
    }

    public static void main(String[] args) {
        System.out.println("Android upload resume: " + run() + " checks passed.");
    }

    private static void require(boolean condition) {
        if(!condition) throw new AssertionError("Android upload resume failed");
    }
}
