package dev.personal.remotedesktop;

import android.content.*;
import android.database.*;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;
import java.io.*;

/** Read-only content URIs with per-recipient grants; never exposes arbitrary paths. */
public final class ClipboardProvider extends ContentProvider {
    static final String AUTHORITY="dev.personal.remotedesktop.clipboard";
    @Override public boolean onCreate() { return true; }
    private File resolve(Uri uri) throws FileNotFoundException {
        try {
            java.util.List<String> parts=uri.getPathSegments();
            if(!AUTHORITY.equals(uri.getAuthority()) || parts.size()!=3 || !parts.get(0).matches("[0-9a-f-]{36}") || !parts.get(1).matches("[0-9]{1,2}") || parts.get(2).contains("/") || parts.get(2).contains("\\") || parts.get(2).equals(".."))throw new IOException();
            File root=new File(getContext().getFilesDir(),"clipboard").getCanonicalFile(); File file=new File(new File(new File(root,parts.get(0)),parts.get(1)),parts.get(2)).getCanonicalFile();
            if(!file.getPath().startsWith(root.getPath()+File.separator) || !file.isFile())throw new IOException(); return file;
        } catch(IOException e) { throw new FileNotFoundException("Invalid clipboard URI"); }
    }
    @Override public ParcelFileDescriptor openFile(Uri uri,String mode) throws FileNotFoundException {
        if(!"r".equals(mode))throw new FileNotFoundException("Read-only clipboard"); return ParcelFileDescriptor.open(resolve(uri),ParcelFileDescriptor.MODE_READ_ONLY);
    }
    @Override public String getType(Uri uri) { return "application/octet-stream"; }
    @Override public Cursor query(Uri uri,String[] projection,String selection,String[] args,String order) {
        try {
            File file=resolve(uri); String[] columns=projection==null?new String[]{OpenableColumns.DISPLAY_NAME,OpenableColumns.SIZE}:projection;
            MatrixCursor result=new MatrixCursor(columns); Object[] row=new Object[columns.length];
            for(int i=0;i<columns.length;i++)row[i]=OpenableColumns.DISPLAY_NAME.equals(columns[i])?file.getName():OpenableColumns.SIZE.equals(columns[i])?file.length():null;
            result.addRow(row); return result;
        } catch(FileNotFoundException e) { return null; }
    }
    @Override public Uri insert(Uri uri,ContentValues values) { throw new UnsupportedOperationException(); }
    @Override public int delete(Uri uri,String selection,String[] args) { throw new UnsupportedOperationException(); }
    @Override public int update(Uri uri,ContentValues values,String selection,String[] args) { throw new UnsupportedOperationException(); }
}
