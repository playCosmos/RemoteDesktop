package dev.personal.remotedesktop;

import android.app.*;
import android.content.*;
import android.database.Cursor;
import android.net.Uri;
import android.provider.*;
import org.json.*;
import java.io.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;
import java.util.function.*;

final class ClipboardFiles {
    private final Activity activity;
    private final Supplier<RemoteApi> server;
    private final Consumer<String> status;
    private final ExecutorService worker;
    private final ClipboardManager clipboard;
    private final AtomicInteger revision=new AtomicInteger();
    private final AtomicBoolean busy=new AtomicBoolean();
    private final AtomicBoolean textSyncing=new AtomicBoolean();
    private final AtomicBoolean stop=new AtomicBoolean();
    private final String origin=UUID.randomUUID().toString();
    private final ClipboardManager.OnPrimaryClipChangedListener listener=() -> revision.incrementAndGet();
    private ArrayList<Uri> pending=new ArrayList<>();
    private final Set<String> downloading=Collections.synchronizedSet(new HashSet<>());
    private int observedTextSequence=-1;
    private boolean sawTextSequence;
    private String remoteTextRevision="";
    private static final String TEXT_MARKER="PersonalRemoteDesktop:text:";
    ClipboardFiles(Activity activity,Supplier<RemoteApi> server,Consumer<String> status,ExecutorService worker) {
        this.activity=activity; this.server=server; this.status=status; this.worker=worker;
        clipboard=(ClipboardManager)activity.getSystemService(Context.CLIPBOARD_SERVICE); clipboard.addPrimaryClipChangedListener(listener);
        try { retireUnused(); } catch(RuntimeException ignored) {}
    }
    void dispose() { clipboard.removePrimaryClipChangedListener(listener); }
    void syncText() {
        RemoteApi api=server.get(); if(api==null || !textSyncing.compareAndSet(false,true))return;
        worker.execute(() -> { try { syncText(api); } catch(Exception e) { status.accept("텍스트 클립보드: "+e.getMessage()); } finally { textSyncing.set(false); } });
    }
    private void syncText(RemoteApi api) throws Exception {
        int now=revision.get(); ClipData local=clipboard.getPrimaryClip(); ClipDescription description=clipboard.getPrimaryClipDescription();
        String text="",label="";
        if(local!=null && description!=null && description.hasMimeType("text/plain") && local.getItemCount()>0 && local.getItemAt(0).getText()!=null) {
            text=local.getItemAt(0).getText().toString(); label=String.valueOf(description.getLabel());
        }
        JSONObject snapshot=api.clipboard();
        if(now!=observedTextSequence) {
            boolean first=!sawTextSequence;
            sawTextSequence=true;
            observedTextSequence=now;
            boolean own=label.startsWith(TEXT_MARKER);
            if(!own && text.length()<=1000000 && copied().isEmpty() && (!text.isEmpty() || !first)) {
                JSONObject published=api.setClipboardText(text,origin,snapshot.getString("id")); remoteTextRevision=published.getString("id"); status.accept(text.isEmpty()?"클립보드를 지웠습니다.":"텍스트 클립보드 동기화 완료"); return;
            }
        }
        if(snapshot.getString("id").equals(remoteTextRevision))return;
        JSONArray files=snapshot.getJSONArray("files"); String remoteText=snapshot.optString("text","");
        if(origin.equals(snapshot.optString("origin","")) || files.length()!=0) { remoteTextRevision=snapshot.getString("id"); return; }
        if(remoteText.isEmpty()) {
            activity.runOnUiThread(() -> {
                if(server.get()!=api || revision.get()!=now)return;
                try { clipboard.clearPrimaryClip(); }
                catch(RuntimeException e) { status.accept("클립보드: "+e.getMessage()); return; }
                observedTextSequence=revision.get(); remoteTextRevision=snapshot.optString("id",""); retireUnused(); status.accept("클립보드를 지웠습니다.");
            });
            return;
        }
        if(revision.get()!=now)return;
        activity.runOnUiThread(() -> {
            if(server.get()!=api || revision.get()!=now)return;
            clipboard.setPrimaryClip(ClipData.newPlainText(TEXT_MARKER+snapshot.optString("id",""),remoteText));
            observedTextSequence=revision.get(); remoteTextRevision=snapshot.optString("id",""); retireUnused(); status.accept("텍스트 클립보드 수신 완료");
        });
    }
    void accept(Intent intent) {
        if(!Intent.ACTION_SEND.equals(intent.getAction()) && !Intent.ACTION_SEND_MULTIPLE.equals(intent.getAction()))return;
        pending=new ArrayList<>();
        if(Intent.ACTION_SEND_MULTIPLE.equals(intent.getAction())) { ArrayList<Uri> values=intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM); if(values!=null)pending.addAll(values); }
        else { Uri uri=intent.getParcelableExtra(Intent.EXTRA_STREAM); if(uri!=null)pending.add(uri); }
        pending.removeIf(uri -> !"content".equals(uri.getScheme()));
        status.accept("공유 파일 "+pending.size()+"개 준비됨 · 연결 후 클립보드 메뉴에서 원격에 붙여넣으세요.");
    }
    private ArrayList<Uri> copied() {
        if(!pending.isEmpty())return new ArrayList<>(pending);
        ArrayList<Uri> uris=new ArrayList<>(); ClipData clip=clipboard.getPrimaryClip();
        if(clip!=null)for(int i=0;i<clip.getItemCount();i++) { Uri uri=clip.getItemAt(i).getUri(); if(uri!=null && "content".equals(uri.getScheme()))uris.add(uri); }
        return uris;
    }
    void menu() {
        new AlertDialog.Builder(activity).setTitle("파일 클립보드").setItems(new String[]{"복사한 파일 → 원격에 붙여넣기","원격에서 복사한 파일 가져오기","복사한 파일 → 이 기기 폴더에 붙여넣기","전송 취소"},(d,index) -> {
            if(index==0)pasteRemote(); else if(index==1)copyRemote(); else if(index==2)activity.startActivityForResult(new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE),7); else requestStop();
        }).show();
    }
    private void requestStop() {
        stop.set(true);
        RemoteApi api=server.get();
        if(api!=null)api.cancelled=true;
    }
    private String name(Uri uri) throws IOException {
        try(Cursor c=activity.getContentResolver().query(uri,null,null,null,null)) {
            if(c!=null && c.moveToFirst()) { String name=c.getString(c.getColumnIndexOrThrow(OpenableColumns.DISPLAY_NAME)); validateName(name); return name; }
        }
        throw new IOException("파일 이름을 읽을 수 없습니다.");
    }
    private static void validateName(String name) throws IOException {
        if(name==null || name.isEmpty() || name.length()>240 || name.indexOf(92)>=0 || name.indexOf(':')>=0)throw new IOException("지원하지 않는 파일 이름");
        String[] parts=name.split("/", -1);
        if(parts.length==0 || parts.length>32)throw new IOException("지원하지 않는 파일 이름");
        for(String part:parts) if(part.isEmpty() || part.equals(".") || part.equals("..") || part.length()>200 || part.endsWith(".") || part.endsWith(" "))throw new IOException("지원하지 않는 파일 이름");
    }
    private void work(RemoteApi api,Runnable task) {
        if(!busy.compareAndSet(false,true)) { status.accept("클립보드 전송 중입니다."); return; }
        stop.set(false);
        if(api!=null)api.cancelled=false;
        worker.execute(() -> { try { task.run(); } finally { busy.set(false); } });
    }
    private void pasteRemote() {
        RemoteApi api=server.get(); if(api==null) { status.accept("서버에 먼저 접속하세요."); return; }
        ArrayList<Uri> uris=copied();
        if(uris.isEmpty() || uris.size()>100) { status.accept("파일을 복사하거나 공유 → Remote Desktop을 선택하세요. 최대 100개입니다."); return; }
        work(api,() -> {
            File staging=new File(activity.getCacheDir(),"clipboard-"+UUID.randomUUID()); staging.mkdirs();
            try {
                ArrayList<File> files=new ArrayList<>(); ArrayList<String> names=new ArrayList<>(); long total=0;
                for(int i=0;i<uris.size();i++) {
                    String name=name(uris.get(i)); File target=new File(staging,Integer.toString(i)); files.add(target); names.add(name);
                    try(InputStream in=activity.getContentResolver().openInputStream(uris.get(i)); OutputStream out=new FileOutputStream(target)) {
                        byte[] b=new byte[65536]; int n; long length=0;
                        while((n=in.read(b))!=-1) { if(api.cancelled)throw new IOException("전송 취소됨"); length+=n; total+=n; if(length>1073741824L || total>4294967296L)throw new IOException("파일당 1GiB, 전체 4GiB 제한"); out.write(b,0,n); }
                    }
                }
                api.uploadClipboard(files,names,origin,bytes -> progress(bytes));
                if(api.cancelled || server.get()!=api)throw new IOException("연결이 종료되었습니다.");
                api.input(new JSONObject().put("kind","release"));
                for(int[] key:new int[][]{{17,1},{86,1},{86,0},{17,0}})api.input(new JSONObject().put("kind","key").put("code",key[0]).put("down",key[1]==1));
                status.accept("원격 클립보드 파일 붙여넣기 완료"); activity.runOnUiThread(() -> pending.clear());
            } catch(Exception e) { status.accept("클립보드: "+e.getMessage()); }
            finally { remove(staging); }
        });
    }
    private void copyRemote() {
        RemoteApi api=server.get(); if(api==null)return; int before=revision.get();
        work(api,() -> {
            File directory=new File(new File(activity.getFilesDir(),"clipboard"),UUID.randomUUID().toString());
            downloading.add(directory.getName());
            try {
                JSONObject snapshot=api.clipboard(); JSONArray files=snapshot.getJSONArray("files"); String id=snapshot.getString("id");
                if(files.length()==0 || files.length()>100)throw new IOException("원격 탐색기에서 파일이나 폴더를 먼저 복사하세요.");
                ArrayList<Uri> uris=new ArrayList<>(); long total=0;
                for(int i=0;i<files.length();i++) {
                    String name=files.getJSONObject(i).getString("name"); validateName(name); long size=files.getJSONObject(i).getLong("size"); total+=size;
                    if(size<0 || size>1073741824L || total>4294967296L)throw new IOException("클립보드 파일 크기 제한");
                    File folder=new File(directory,Integer.toString(i)); folder.mkdirs(); File file=nestedFile(folder, name);
                    try(OutputStream out=new FileOutputStream(file)) { api.downloadClipboard(id,i,out,bytes -> progress(bytes)); }
                    if(file.length()!=size)throw new IOException("파일 크기 불일치");
                    Uri.Builder builder=new Uri.Builder().scheme("content").authority(ClipboardProvider.AUTHORITY).appendPath(directory.getName()).appendPath(Integer.toString(i));
                    for(String part:name.split("/", -1))builder.appendPath(part);
                    uris.add(builder.build());
                }
                if(!api.clipboard().getString("id").equals(id))throw new IOException("원격 클립보드가 변경되었습니다.");
                activity.runOnUiThread(() -> {
                    if(server.get()!=api || revision.get()!=before || api.cancelled) { downloading.remove(directory.getName()); remove(directory); status.accept("새로 복사한 클립보드를 유지했습니다."); return; }
                    ClipData data=ClipData.newRawUri("Remote Desktop files",uris.get(0)); for(int i=1;i<uris.size();i++)data.addItem(new ClipData.Item(uris.get(i)));
                    pending.clear(); clipboard.setPrimaryClip(data); retireUnused(); downloading.remove(directory.getName()); status.accept("파일이 클립보드에 복사됐습니다. 지원 앱 또는 ‘이 기기 폴더에 붙여넣기’를 사용하세요.");
                });
            } catch(Exception e) { downloading.remove(directory.getName()); remove(directory); status.accept("클립보드: "+e.getMessage()); }
        });
    }
    void pasteFolder(Uri tree) {
        RemoteApi api=server.get();
        ArrayList<Uri> uris=copied(); if(uris.isEmpty()) { status.accept("클립보드에 파일이 없습니다."); return; }
        work(api,() -> {
            ArrayList<Uri> created=new ArrayList<>();
            boolean cancel=false;
            try {
                status.accept("폴더에 붙여넣는 중");
                Uri parent=DocumentsContract.buildDocumentUriUsingTree(tree,DocumentsContract.getTreeDocumentId(tree));
                java.util.HashMap<String,Uri> dirs=new java.util.HashMap<>();
                dirs.put("", parent);
                long written=0;
                for(Uri uri:uris) {
                    if(stop.get()) { cancel=true; break; }
                    java.util.List<String> parts=uri.getPathSegments();
                    if(parts.size()<3)throw new IOException("지원하지 않는 파일 이름");
                    Uri dir=parent; String key="";
                    for(int i=2;i<parts.size()-1;i++) {
                        if(stop.get()) { cancel=true; break; }
                        key=key+"/"+parts.get(i);
                        Uri next=dirs.get(key);
                        if(next==null) {
                            next=DocumentsContract.createDocument(activity.getContentResolver(),dir,DocumentsContract.Document.MIME_TYPE_DIR,parts.get(i));
                            if(next==null)throw new IOException("폴더를 만들지 못했습니다");
                            created.add(next);
                            dirs.put(key, next);
                        }
                        dir=next;
                    }
                    if(cancel) break;
                    Uri destination=DocumentsContract.createDocument(activity.getContentResolver(),dir,"application/octet-stream",parts.get(parts.size()-1));
                    if(destination==null)throw new IOException("파일 생성 실패");
                    created.add(destination);
                    try(InputStream in=activity.getContentResolver().openInputStream(uri); OutputStream out=activity.getContentResolver().openOutputStream(destination,"w")) {
                        if(in==null || out==null)throw new IOException("파일을 열지 못했습니다");
                        byte[] b=new byte[65536]; int n;
                        while((n=in.read(b))!=-1) {
                            if(stop.get()) { cancel=true; break; }
                            out.write(b,0,n); written+=n; progress(written);
                        }
                    }
                    catch(Exception e) { DocumentsContract.deleteDocument(activity.getContentResolver(),destination); created.remove(destination); throw e; }
                    if(cancel) break;
                }
                if(cancel) { rollback(created); status.accept("붙여넣기를 취소했습니다."); return; }
                status.accept("클립보드 파일 붙여넣기 완료");
            } catch(Exception e) { status.accept("붙여넣기: "+e.getMessage()); }
        });
    }
    private void rollback(List<Uri> created) {
        for(int i=created.size()-1;i>=0;i--) {
            try { DocumentsContract.deleteDocument(activity.getContentResolver(),created.get(i)); }
            catch(Exception ignored) {}
        }
    }
    private long lastProgress;
    private void progress(long bytes) { long now=android.os.SystemClock.elapsedRealtime(); if(now-lastProgress>250) { lastProgress=now; status.accept(String.format("클립보드 전송: %.1f MB",bytes/1048576.0)); } }
    private static void remove(File file) { File[] children=file.listFiles(); if(children!=null)for(File child:children)remove(child); file.delete(); }
    private void retireUnused() {
        java.util.HashSet<String> keep=new java.util.HashSet<>();
        ClipData clip=clipboard.getPrimaryClip();
        if(clip!=null) for(int i=0;i<clip.getItemCount();i++) {
            Uri uri=clip.getItemAt(i).getUri();
            if(uri==null || !ClipboardProvider.AUTHORITY.equals(uri.getAuthority())) continue;
            java.util.List<String> parts=uri.getPathSegments();
            if(!parts.isEmpty() && parts.get(0).matches("[0-9a-fA-F-]{36}")) keep.add(parts.get(0));
        }
        File root=new File(activity.getFilesDir(),"clipboard");
        File[] children=root.listFiles();
        if(children==null) return;
        for(File child:children) if(child.isDirectory() && !keep.contains(child.getName()) && !downloading.contains(child.getName())) remove(child);
    }

    private static File nestedFile(File root, String name) throws IOException {
        File current=root;
        String[] parts=name.split("/", -1);
        for(int i=0;i<parts.length-1;i++) {
            current=new File(current, parts[i]);
            if(!current.isDirectory() && !current.mkdirs())throw new IOException("폴더를 만들지 못했습니다");
        }
        File file=new File(current, parts[parts.length-1]);
        String rootPath=root.getCanonicalPath()+File.separator;
        if(!file.getCanonicalPath().startsWith(rootPath))throw new IOException("지원하지 않는 파일 이름");
        return file;
    }
}
