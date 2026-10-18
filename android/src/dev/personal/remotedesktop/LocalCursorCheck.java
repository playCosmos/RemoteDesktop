package dev.personal.remotedesktop;

final class LocalCursorCheck {
    private LocalCursorCheck() {}

    static int run() {
        int checks=0;
        float[] full=LocalCursor.inView(0.25f,0.75f,0f,0f,1f,1f);
        require(full!=null && Math.abs(full[0]-0.25f)<0.0001f && Math.abs(full[1]-0.75f)<0.0001f); checks++;
        float[] zoom=LocalCursor.inView(0.25f,0.75f,0.2f,0.5f,0.5f,0.5f);
        require(zoom!=null && Math.abs(zoom[0]-0.1f)<0.0001f && Math.abs(zoom[1]-0.5f)<0.0001f); checks++;
        require(LocalCursor.inView(0.1f,0.75f,0.2f,0.5f,0.5f,0.5f)==null); checks++;
        require(LocalCursor.inView(Float.NaN,0.5f,0f,0f,1f,1f)==null); checks++;
        LocalCursor.Mark mark=LocalCursor.place(0.5f,0.5f,2,4,16,16,200f,100f,200f,200f);
        require(mark.shown && Math.abs(mark.left-98f)<0.01f && Math.abs(mark.top-96f)<0.01f && mark.width==16f && mark.height==16f); checks++;
        require(!LocalCursor.place(1.2f,0.5f,0,0,16,16,200f,100f,200f,200f).shown); checks++;
        require(!LocalCursor.place(Float.NaN,0.5f,0,0,16,16,200f,100f,200f,200f).shown); checks++;
        require(!LocalCursor.place(0.5f,0.5f,0,0,16,16,200f,100f,Float.POSITIVE_INFINITY,200f).shown); checks++;
        return checks;
    }

    public static void main(String[] args) {
        System.out.println("Android local cursor geometry: " + run() + " checks passed.");
    }

    private static void require(boolean condition) {
        if(!condition) throw new AssertionError("Android local cursor geometry failed");
    }
}
