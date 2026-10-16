package dev.personal.remotedesktop;

import android.app.Application;
import android.util.Log;
import java.io.File;
import java.io.FileOutputStream;

/** Captures startup crashes before MainActivity is constructed. */
public final class RemoteDesktopApplication extends Application {
    private static volatile File crashFile;

    @Override public void onCreate() {
        super.onCreate();
        crashFile=new File(getFilesDir(),"last-crash.txt");
        final Thread.UncaughtExceptionHandler previous=Thread.getDefaultUncaughtExceptionHandler();
        Thread.setDefaultUncaughtExceptionHandler((thread,error) -> {
            try (FileOutputStream output=new FileOutputStream(crashFile,false)) {
                byte[] bytes=Log.getStackTraceString(error).getBytes("UTF-8");
                output.write(bytes,0,Math.min(bytes.length,16000));
                output.flush();
                output.getFD().sync();
            } catch(Exception ignored) {}
            if(previous!=null) previous.uncaughtException(thread,error);
        });
    }

    static File previousCrashFile() { return crashFile; }
}
