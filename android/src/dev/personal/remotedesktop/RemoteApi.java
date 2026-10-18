package dev.personal.remotedesktop;

import org.json.*;
import javax.net.ssl.*;
import java.net.*;
import java.io.*;
import java.security.*;
import java.security.cert.*;

final class RemoteApi {
    private final URL base;
    private final SSLSocketFactory sockets;
    private final byte[] pin;
    private volatile String token = "";
    volatile boolean cancelled;
    interface Progress { void update(long bytes); }
    static final class StatusException extends IOException {
        final int code;
        StatusException(int code, String message) { super(message); this.code = code; }
    }

    RemoteApi(String address, String fingerprint) throws Exception {
        String hex = fingerprint.replace(":", "").replace(" ", "").trim();
        if (!hex.matches("[0-9a-fA-F]{64}")) throw new IOException("서버 지문 64자리를 입력하세요.");
        pin = new byte[32];
        for (int i = 0; i < 32; i++) pin[i] = (byte)Integer.parseInt(hex.substring(i*2, i*2+2), 16);
        base = new URL("https://" + address.trim() + "/v1/");
        if (!base.getPath().equals("/v1/") || base.getUserInfo() != null || base.getQuery() != null || base.getRef() != null) throw new IOException("주소는 호스트:포트 형식입니다.");
        TrustManager[] trust = { new X509TrustManager() {
            public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
            public void checkClientTrusted(X509Certificate[] chain, String auth) throws CertificateException { throw new CertificateException("Client certificates unsupported"); }
            public void checkServerTrusted(X509Certificate[] chain, String auth) throws CertificateException {
                if (chain == null || chain.length == 0 || !matches(chain[0])) throw new CertificateException("서버 지문 불일치");
                chain[0].checkValidity();
            }
        }};
        SSLContext context = SSLContext.getInstance("TLS"); context.init(null, trust, new SecureRandom()); sockets = context.getSocketFactory();
    }
    private boolean matches(java.security.cert.Certificate certificate) {
        try { return MessageDigest.isEqual(pin, MessageDigest.getInstance("SHA-256").digest(certificate.getEncoded())); }
        catch (Exception e) { return false; }
    }
    private HttpsURLConnection open(String path, String method) throws Exception { return open(path, method, 15000); }
    private HttpsURLConnection open(String path, String method, int readTimeout) throws Exception {
        HttpsURLConnection c = (HttpsURLConnection)new URL(base, path).openConnection();
        c.setSSLSocketFactory(sockets);
        c.setHostnameVerifier((host, session) -> { try { return matches(session.getPeerCertificates()[0]); } catch (Exception e) { return false; } });
        c.setInstanceFollowRedirects(false); c.setConnectTimeout(10000); c.setReadTimeout(readTimeout);
        c.setRequestMethod(method); c.setUseCaches(false);
        if (!token.isEmpty()) c.setRequestProperty("Authorization", "Bearer " + token);
        return c;
    }
    private static void check(HttpsURLConnection c) throws Exception {
        int code = c.getResponseCode();
        if (code < 200 || code >= 300) throw new StatusException(code, code == 401 ? "비밀번호 오류 또는 세션 종료" : code == 409 ? "서버 사용 중 또는 같은 이름의 파일 존재" : code == 429 ? "접속 시도 제한: 잠시 후 다시 시도하세요." : "서버 오류 HTTP " + code);
    }
    void ping() throws Exception {
        HttpsURLConnection c = open("ping", "GET", 5000);
        try {
            check(c);
            try (InputStream in = c.getInputStream()) { byte[] buffer = new byte[256]; while (in.read(buffer) != -1) {} }
        } finally { c.disconnect(); }
    }
    byte[] request(String path, String method, JSONObject body) throws Exception {
        HttpsURLConnection c = open(path, method);
        try {
            if (body != null) {
                c.setDoOutput(true); c.setRequestProperty("Content-Type", "application/json");
                byte[] bytes = body.toString().getBytes("UTF-8"); c.setFixedLengthStreamingMode(bytes.length);
                try (OutputStream out = c.getOutputStream()) { out.write(bytes); }
            }
            check(c);
            try (InputStream in = c.getInputStream(); ByteArrayOutputStream out = new ByteArrayOutputStream()) {
                byte[] buffer = new byte[65536]; int n;
                while ((n = in.read(buffer)) != -1) { if (out.size() + n > 32*1024*1024) throw new IOException("응답 크기 초과"); out.write(buffer,0,n); }
                return out.toByteArray();
            }
        } finally { c.disconnect(); }
    }
    static final class Picture {
        final byte[] bytes;
        final String codec;
        final double transferMs;
        final String secure;
        final Cursor cursor;
        Picture(byte[] bytes, String codec, double transferMs, String secure, Cursor cursor) {
            this.bytes = bytes;
            this.codec = codec == null || codec.trim().isEmpty() ? "jpeg" : codec.trim();
            this.transferMs = transferMs;
            this.secure = SecureNotice.state(secure);
            this.cursor = cursor == null ? new Cursor(false, false, 0, 0, "", null, Double.NaN, Double.NaN) : cursor;
        }
    }
    static final class Cursor {
        final boolean local;
        final boolean visible;
        final int hotX;
        final int hotY;
        final String hash;
        final byte[] png;
        final double x;
        final double y;
        final boolean hasPosition;
        Cursor(boolean local, boolean visible, int hotX, int hotY, String hash, byte[] png, double x, double y) {
            this.local = local;
            this.visible = visible;
            this.hotX = hotX;
            this.hotY = hotY;
            this.hash = hash == null ? "" : hash;
            this.png = png;
            this.x = x;
            this.y = y;
            this.hasPosition = Double.isFinite(x) && Double.isFinite(y);
        }
        static Cursor read(HttpsURLConnection connection) {
            String encoded = connection.getHeaderField("X-Cursor-Png");
            byte[] png = null;
            if (encoded != null && !encoded.isEmpty() && encoded.length() <= 24000) {
                try { png = android.util.Base64.decode(encoded, android.util.Base64.DEFAULT); }
                catch (IllegalArgumentException ignored) { png = null; }
            }
            return new Cursor("1".equals(connection.getHeaderField("X-Cursor-Local")), "1".equals(connection.getHeaderField("X-Cursor-Visible")), headerInt(connection.getHeaderField("X-Cursor-Hot-X")), headerInt(connection.getHeaderField("X-Cursor-Hot-Y")), connection.getHeaderField("X-Cursor-Hash"), png, headerDouble(connection.getHeaderField("X-Cursor-X")), headerDouble(connection.getHeaderField("X-Cursor-Y")));
        }
    }
    Picture frame(String path) throws Exception {
        long started = System.currentTimeMillis();
        HttpsURLConnection c = open(path, "GET", 15000);
        try {
            check(c);
            String codec = c.getHeaderField("X-Codec");
            String secure = c.getHeaderField("X-Secure-Desktop");
            Cursor cursor = Cursor.read(c);
            double capture = headerMillis(c.getHeaderField("X-Capture-Ms"));
            double encode = headerMillis(c.getHeaderField("X-Encode-Ms"));
            double wait = headerMillis(c.getHeaderField("X-Wait-Ms"));
            try (InputStream in = c.getInputStream(); ByteArrayOutputStream out = new ByteArrayOutputStream()) {
                byte[] buffer = new byte[65536]; int n;
                while ((n = in.read(buffer)) != -1) {
                    if (out.size() + n > 32 * 1024 * 1024) throw new IOException("응답 크기 초과");
                    out.write(buffer, 0, n);
                }
                byte[] bytes = out.toByteArray();
                double spent = System.currentTimeMillis() - started;
                double transfer = bytes.length == 0 ? -1 : Math.max(0, spent - capture - encode - wait);
                return new Picture(bytes, codec, transfer, secure, cursor);
            }
        } finally { c.disconnect(); }
    }
    private static double headerMillis(String value) {
        if (value == null || value.isEmpty()) return 0;
        try { return Double.parseDouble(value); }
        catch (NumberFormatException error) { return 0; }
    }
    private static int headerInt(String value) {
        if (value == null || value.isEmpty()) return 0;
        try { return Integer.parseInt(value.trim()); }
        catch (NumberFormatException error) { return 0; }
    }
    private static double headerDouble(String value) {
        if (value == null || value.isEmpty()) return Double.NaN;
        try { return Double.parseDouble(value.trim()); }
        catch (NumberFormatException error) { return Double.NaN; }
    }
    void login(String password) throws Exception {
        JSONObject reply = new JSONObject(new String(request("login", "POST", new JSONObject().put("password",password)), "UTF-8"));
        if (reply.getInt("protocol") != 1) throw new IOException("프로토콜 버전 불일치");
        token = reply.getString("token");
    }
    String input(JSONObject event) throws Exception {
        byte[] body = request("input", "POST", event);
        if (body == null || body.length == 0) return null;
        String text = new String(body, "UTF-8").trim();
        if (text.isEmpty() || text.charAt(0) != '{') return null;
        String notice = new JSONObject(text).optString("notice", "");
        return notice.isEmpty() ? null : notice;
    }
    void logout() { try { request("logout", "POST", new JSONObject()); } catch (Exception ignored) {} token = ""; }
    JSONObject clipboard() throws Exception { return new JSONObject(new String(request("clipboard", "GET", null), "UTF-8")); }
    JSONObject setClipboardText(String text,String origin,String expectedRevision) throws Exception { return setClipboardText(text,origin,expectedRevision,""); }
    JSONObject setClipboardText(String text,String origin,String expectedRevision,String html) throws Exception {
        if(text==null || text.length()>1000000)throw new IOException("클립보드 텍스트는 100만 자까지 지원합니다.");
        if(html==null)html="";
        if(html.length()>1000000)throw new IOException("서식 있는 텍스트는 100만 자까지 지원합니다.");
        JSONObject body=new JSONObject().put("text",text).put("origin",origin).put("expectedRevision",expectedRevision);
        if(!html.isEmpty())body.put("html",html);
        return new JSONObject(new String(request("clipboard/text","PUT",body),"UTF-8"));
    }
    JSONObject setClipboardImage(byte[] png,String origin,String expectedRevision) throws Exception {
        if(png==null || png.length==0 || png.length>16*1024*1024)throw new IOException("클립보드 이미지는 16MB 이하만 지원합니다.");
        HttpsURLConnection c=open("clipboard/image","PUT",60000);
        try {
            c.setDoOutput(true); c.setFixedLengthStreamingMode(png.length); c.setRequestProperty("Content-Type","image/png");
            c.setRequestProperty("X-Content-SHA256",hex(MessageDigest.getInstance("SHA-256").digest(png)));
            c.setRequestProperty("X-Clipboard-Origin",origin); c.setRequestProperty("X-Clipboard-Revision",expectedRevision);
            try(OutputStream out=c.getOutputStream()) { out.write(png); }
            check(c);
            try(InputStream in=c.getInputStream(); ByteArrayOutputStream body=new ByteArrayOutputStream()) {
                byte[] buffer=new byte[8192]; int n; while((n=in.read(buffer))!=-1) body.write(buffer,0,n);
                return new JSONObject(new String(body.toByteArray(),"UTF-8"));
            }
        } finally { c.disconnect(); }
    }
    void uploadClipboard(java.util.List<File> files, java.util.List<String> names, String origin, Progress progress) throws Exception {
        JSONArray metadata=new JSONArray(); long total=0;
        if(files.isEmpty() || files.size()>100)throw new IOException("파일 1~100개를 복사하세요.");
        for(int i=0;i<files.size();i++) {
            boolean directory=files.get(i).isDirectory(); long size=directory?0:files.get(i).length();
            if(!directory && size>1073741824L)throw new IOException("파일당 최대 1GiB입니다.");
            total+=size;
            JSONObject item=new JSONObject().put("name",names.get(i)).put("size",size);
            if(directory)item.put("directory",true);
            metadata.put(item);
        }
        if(total>4294967296L)throw new IOException("한 번에 최대 4GiB입니다.");
        JSONObject begin=new JSONObject(new String(request("clipboard/uploads","POST",new JSONObject().put("origin",origin).put("expectedRevision",clipboard().getString("id")).put("files",metadata)),"UTF-8"));
        String id=begin.getString("id"); boolean committed=false;
        try {
            for(int i=0;i<files.size();i++) {
                if(files.get(i).isDirectory())continue;
                if(cancelled)throw new IOException("전송 취소됨");
                MessageDigest hash=MessageDigest.getInstance("SHA-256"); byte[] buffer=new byte[65536]; int n;
                try(InputStream input=new FileInputStream(files.get(i))) { while((n=input.read(buffer))!=-1) { if(cancelled)throw new IOException("전송 취소됨"); hash.update(buffer,0,n); } }
                String fileHash=hex(hash.digest());
                long offset=0; int attempt=0;
                while(true) {
                    if(cancelled)throw new IOException("전송 취소됨");
                    if(attempt++>0) {
                        if(attempt>4)throw new IOException("클립보드 전송을 이어받지 못했습니다.");
                        long probed=receivedClipboard(id,i);
                        long resumed=UploadResume.nextOffset(offset,probed,files.get(i).length());
                        if(resumed<0) throw new IOException("클립보드 전송을 이어받지 못했습니다.");
                        offset=resumed;
                        if(UploadResume.complete(offset,files.get(i).length())) break;
                    }
                    HttpsURLConnection c=open("clipboard/uploads/"+id+"/"+i,"PUT");
                    try {
                        c.setDoOutput(true); c.setFixedLengthStreamingMode(files.get(i).length()-offset); c.setRequestProperty("X-Content-SHA256",fileHash); c.setRequestProperty("X-Upload-Offset",Long.toString(offset));
                        try(InputStream in=new FileInputStream(files.get(i)); OutputStream out=c.getOutputStream()) {
                            long skip=offset;
                            while(skip>0) { long skipped=in.skip(skip); if(skipped<=0) { if(in.read()<0) throw new IOException("클립보드 전송을 이어받지 못했습니다."); skipped=1; } skip-=skipped; }
                            int read; long count=offset;
                            while((read=in.read(buffer))!=-1) { if(cancelled)throw new IOException("전송 취소됨"); out.write(buffer,0,read); count+=read; progress.update(count); }
                        }
                        check(c);
                        break;
                    } catch(IOException e) { if(cancelled || attempt>=4) throw e; }
                    finally { c.disconnect(); }
                }
            }
            if(cancelled)throw new IOException("전송 취소됨");
            request("clipboard/uploads/"+id+"/commit","POST",new JSONObject()); committed=true;
        } finally { if(!committed)try { request("clipboard/uploads/"+id,"DELETE",null); } catch(Exception ignored) {} }
    }
    void downloadClipboard(String id,int index,OutputStream out,Progress progress) throws Exception { downloadPath("clipboard/"+URLEncoder.encode(id,"UTF-8")+"/"+index,out,1024L*1024*1024,progress); }
    void downloadClipboardFile(String id,int index,File file,Progress progress) throws Exception {
        String path="clipboard/"+URLEncoder.encode(id,"UTF-8")+"/"+index;
        int attempt=0; long stuck=-1;
        while(true) {
            if(cancelled)throw new IOException("전송 취소됨");
            long start=file.isFile()?file.length():0;
            HttpsURLConnection c=open(path,"GET");
            try {
                if(start>0)c.setRequestProperty("Range","bytes="+start+"-");
                check(c);
                int code=c.getResponseCode(); boolean partial=code==206;
                if(!partial && start>0) { file.delete(); start=0; }
                MessageDigest hash=MessageDigest.getInstance("SHA-256");
                if(partial) { try(InputStream prefix=new FileInputStream(file)) { byte[] buffer=new byte[65536]; int n; while((n=prefix.read(buffer))!=-1) hash.update(buffer,0,n); } }
                long total=partial?start:0;
                try(InputStream in=c.getInputStream(); OutputStream out=new FileOutputStream(file, partial)) {
                    byte[] buffer=new byte[65536]; int n;
                    while((n=in.read(buffer))!=-1) {
                        if(cancelled)throw new IOException("전송 취소됨");
                        total+=n; if(total>1024L*1024*1024)throw new IOException("최대 파일 크기 초과");
                        hash.update(buffer,0,n); out.write(buffer,0,n); progress.update(total);
                    }
                }
                if(!hex(hash.digest()).equalsIgnoreCase(c.getHeaderField("X-Content-SHA256"))) { file.delete(); throw new IOException("파일 해시 불일치"); }
                return;
            } catch(IOException e) {
                long now=file.isFile()?file.length():0;
                if(cancelled || attempt++>=3 || now<=stuck || "파일 해시 불일치".equals(e.getMessage()) || "최대 파일 크기 초과".equals(e.getMessage())) throw e;
                stuck=now;
            } finally { c.disconnect(); }
        }
    }
    private long receivedClipboard(String id,int index) throws Exception {
        return new JSONObject(new String(request("clipboard/uploads/"+id+"/"+index,"GET",null),"UTF-8")).getLong("received");
    }
    void downloadClipboardImage(String id,OutputStream out,Progress progress) throws Exception { downloadPath("clipboard/"+URLEncoder.encode(id,"UTF-8")+"/image",out,16L*1024*1024,progress); }
    String downloadClipboardHtml(String id) throws Exception {
        ByteArrayOutputStream out=new ByteArrayOutputStream();
        downloadPath("clipboard/"+URLEncoder.encode(id,"UTF-8")+"/html",out,4000000L,bytes -> {},"서식 있는 텍스트는 100만 자까지 지원합니다.");
        String html=new String(out.toByteArray(),"UTF-8");
        if(html.length()>1000000)throw new IOException("서식 있는 텍스트는 100만 자까지 지원합니다.");
        return html;
    }
    private void downloadPath(String path, OutputStream out, long maxBytes, Progress progress) throws Exception {
        downloadPath(path,out,maxBytes,progress,maxBytes<=16L*1024*1024?"클립보드 이미지는 16MB 이하만 지원합니다.":"최대 파일 크기 초과");
    }
    private void downloadPath(String path, OutputStream out, long maxBytes, Progress progress, String limitMessage) throws Exception {
        HttpsURLConnection c = open(path, "GET");
        try {
            check(c); long total = 0; MessageDigest hash = MessageDigest.getInstance("SHA-256");
            try (InputStream in = c.getInputStream()) {
                byte[] buffer = new byte[65536]; int n;
                while ((n = in.read(buffer)) != -1) {
                    if (cancelled) throw new IOException("전송 취소됨");
                    total += n; if (total > maxBytes) throw new IOException(limitMessage);
                    hash.update(buffer,0,n); out.write(buffer,0,n); progress.update(total);
                }
            }
            if (!hex(hash.digest()).equalsIgnoreCase(c.getHeaderField("X-Content-SHA256"))) throw new IOException("파일 해시 불일치");
        } finally { c.disconnect(); }
    }
    private static String hex(byte[] bytes) { StringBuilder b = new StringBuilder(); for (byte v : bytes) b.append(String.format("%02X",v)); return b.toString(); }
}
