package dev.personal.remotedesktop;

final class LocalCursor {
    private LocalCursor() {}

    static float[] inView(float cursorX, float cursorY, float viewX, float viewY, float viewWidth, float viewHeight) {
        if (viewWidth <= 0 || viewHeight <= 0 || Float.isNaN(viewX) || Float.isNaN(viewY) || Float.isInfinite(viewX) || Float.isInfinite(viewY) || Float.isNaN(viewWidth) || Float.isNaN(viewHeight) || Float.isInfinite(viewWidth) || Float.isInfinite(viewHeight)) return null;
        if (Float.isNaN(cursorX) || Float.isNaN(cursorY) || Float.isInfinite(cursorX) || Float.isInfinite(cursorY)) return null;
        float x = (cursorX - viewX) / viewWidth;
        float y = (cursorY - viewY) / viewHeight;
        if (x < 0 || y < 0 || x > 1 || y > 1) return null;
        return new float[]{x, y};
    }

    static final class Mark {
        final float left;
        final float top;
        final float width;
        final float height;
        final boolean shown;
        Mark(float left, float top, float width, float height, boolean shown) {
            this.left = left;
            this.top = top;
            this.width = width;
            this.height = height;
            this.shown = shown;
        }
    }

    static Mark place(float normX, float normY, int hotX, int hotY, int cursorWidth, int cursorHeight, float imageWidth, float imageHeight, float boxWidth, float boxHeight) {
        if (imageWidth <= 0 || imageHeight <= 0 || boxWidth <= 0 || boxHeight <= 0 || cursorWidth <= 0 || cursorHeight <= 0 || Float.isNaN(imageWidth) || Float.isNaN(imageHeight) || Float.isNaN(boxWidth) || Float.isNaN(boxHeight) || Float.isInfinite(imageWidth) || Float.isInfinite(imageHeight) || Float.isInfinite(boxWidth) || Float.isInfinite(boxHeight)) return hidden();
        if (Float.isNaN(normX) || Float.isNaN(normY) || Float.isInfinite(normX) || Float.isInfinite(normY)) return hidden();
        if (normX < 0 || normY < 0 || normX > 1 || normY > 1) return hidden();
        float scale = Math.min(boxWidth / imageWidth, boxHeight / imageHeight);
        float drawnWidth = imageWidth * scale;
        float drawnHeight = imageHeight * scale;
        float pointerX = (boxWidth - drawnWidth) / 2f + normX * drawnWidth;
        float pointerY = (boxHeight - drawnHeight) / 2f + normY * drawnHeight;
        return new Mark(pointerX - hotX * scale, pointerY - hotY * scale, cursorWidth * scale, cursorHeight * scale, true);
    }

    private static Mark hidden() { return new Mark(0, 0, 0, 0, false); }
}
