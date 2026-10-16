package dev.personal.remotedesktop;

import android.content.Context;
import android.text.Editable;
import android.text.InputType;
import android.view.KeyEvent;
import android.view.inputmethod.*;
import android.widget.ImageView;

/** An IME-backed remote surface. Only committed text is sent; composition stays local. */
final class RemoteScreen extends ImageView {
    interface Listener { void text(String text); void key(int code, boolean down); void composing(String text); }
    private final Listener listener;
    private RemoteConnection connection;
    RemoteScreen(Context context, Listener listener) {
        super(context); this.listener=listener; setFocusable(true); setFocusableInTouchMode(true);
    }
    @Override public boolean onCheckIsTextEditor() { return true; }
    @Override public InputConnection onCreateInputConnection(EditorInfo info) {
        info.inputType=InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS;
        info.imeOptions=EditorInfo.IME_FLAG_NO_EXTRACT_UI | EditorInfo.IME_ACTION_NONE;
        info.initialSelStart=info.initialSelEnd=0;
        if(connection!=null) connection.finishComposingText();
        connection=new RemoteConnection(); return connection;
    }
    void finishComposition() { if(connection!=null)connection.finishComposingText(); }
    void resetInput() { if(connection!=null) { connection.active=false; connection.getEditable().clear(); connection=null; } }
    void showKeyboard() { requestFocus(); ((InputMethodManager)getContext().getSystemService(Context.INPUT_METHOD_SERVICE)).showSoftInput(this,InputMethodManager.SHOW_IMPLICIT); }
    @Override public boolean onKeyDown(int key,KeyEvent event) { return hardware(event) || super.onKeyDown(key,event); }
    @Override public boolean onKeyUp(int key,KeyEvent event) { return hardware(event) || super.onKeyUp(key,event); }
    private boolean hardware(KeyEvent event) {
        int code=mapKey(event.getKeyCode()); if(code==0)return false;
        listener.key(code,event.getAction()==KeyEvent.ACTION_DOWN); return true;
    }
    private static int mapKey(int key) {
        if(key>=KeyEvent.KEYCODE_A && key<=KeyEvent.KEYCODE_Z)return 65+key-KeyEvent.KEYCODE_A;
        if(key>=KeyEvent.KEYCODE_0 && key<=KeyEvent.KEYCODE_9)return 48+key-KeyEvent.KEYCODE_0;
        switch(key) {
            case KeyEvent.KEYCODE_DEL:return 8; case KeyEvent.KEYCODE_FORWARD_DEL:return 46;
            case KeyEvent.KEYCODE_ENTER:return 13; case KeyEvent.KEYCODE_TAB:return 9;
            case KeyEvent.KEYCODE_ESCAPE:return 27; case KeyEvent.KEYCODE_SPACE:return 32;
            case KeyEvent.KEYCODE_DPAD_LEFT:return 37; case KeyEvent.KEYCODE_DPAD_RIGHT:return 39;
            case KeyEvent.KEYCODE_DPAD_UP:return 38; case KeyEvent.KEYCODE_DPAD_DOWN:return 40;
            case KeyEvent.KEYCODE_CTRL_LEFT:case KeyEvent.KEYCODE_CTRL_RIGHT:return 17;
            case KeyEvent.KEYCODE_ALT_LEFT:case KeyEvent.KEYCODE_ALT_RIGHT:return 18;
            case KeyEvent.KEYCODE_SHIFT_LEFT:case KeyEvent.KEYCODE_SHIFT_RIGHT:return 16;
            case KeyEvent.KEYCODE_LANGUAGE_SWITCH:return 21; default:return 0;
        }
    }
    private final class RemoteConnection extends BaseInputConnection {
        boolean active=true;
        RemoteConnection() { super(RemoteScreen.this,true); }
        @Override public boolean setComposingText(CharSequence text,int cursor) {
            if(!active)return false; boolean result=super.setComposingText(text,cursor); listener.composing(getEditable().toString()); return result;
        }
        @Override public boolean commitText(CharSequence text,int cursor) {
            if(!active)return false; boolean result=super.commitText(text,cursor); flush(); return result;
        }
        @Override public boolean finishComposingText() {
            if(!active)return false; boolean result=super.finishComposingText(); flush(); return result;
        }
        private void flush() { Editable buffer=getEditable(); String value=buffer.toString(); buffer.clear(); if(!value.isEmpty())listener.text(value); listener.composing(""); }
        @Override public boolean deleteSurroundingText(int before,int after) {
            if(!active || before<0 || after<0 || before>1000 || after>1000)return false;
            if(getEditable().length()>0) { boolean result=super.deleteSurroundingText(before,after); listener.composing(getEditable().toString()); return result; }
            for(int i=0;i<before;i++) { listener.key(8,true); listener.key(8,false); }
            for(int i=0;i<after;i++) { listener.key(46,true); listener.key(46,false); }
            return true;
        }
        @Override public boolean deleteSurroundingTextInCodePoints(int before,int after) { return deleteSurroundingText(before,after); }
        @Override public boolean sendKeyEvent(KeyEvent event) { if(!active)return false; finishComposingText(); return hardware(event); }
        @Override public boolean performEditorAction(int action) { if(!active)return false; finishComposingText(); listener.key(13,true); listener.key(13,false); return true; }
    }
}
