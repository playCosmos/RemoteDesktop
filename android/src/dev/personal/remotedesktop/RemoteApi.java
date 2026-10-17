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
        Picture(byte[] bytes, String codec) {
            this.bytes = bytes;
            this.codec = codec == null || codec.trim().isEmpty() ? "jpeg" : codec.trim();
        }
    }
    Picture frame(String path) throws Exception {
        HttpsURLConnection c = open(path, "GET", 15000);
        try {
            check(c);
            String codec = c.getHeaderField("X-Codec");
            try (InputStream in = c.getInputStream(); ByteArrayOutputStream out = new ByteArrayOutputStream()) {
                byte[] buffer = new byte[65536]; int n;
                while ((n = in.read(buffer)) != -1) {
                    if (out.size() + n > 32 * 1024 * 1024) throw new IOException("응답 크기 초과");
                    out.write(buffer, 0, n);
                }
                return new Picture(out.toByteArray(), codec);
            }
        } finally { c.disconnect(); }
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
    JSONObject setClipboardText(String text,String origin,String expectedRevision) throws Exception {
        if(text==null || text.length()>1000000)throw new IOException("클립보드 텍스트는 100만 자까지 지원합니다.");
        return new JSONObject(new String(request("clipboard/text","PUT",new JSONObject().put("text",text).put("origin",origin).put("expectedRevision",expectedRevision)),"UTF-8"));
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
                HttpsURLConnection c=open("clipboard/uploads/"+id+"/"+i,"PUT");
                try {
                    c.setDoOutput(true); c.setFixedLengthStreamingMode(files.get(i).length()); c.setRequestProperty("X-Content-SHA256",hex(hash.digest()));
                    try(InputStream in=new FileInputStream(files.get(i)); OutputStream out=c.getOutputStream()) { long count=0; while((n=in.read(buffer))!=-1) { if(cancelled)throw new IOException("전송 취소됨"); out.write(buffer,0,n); count+=n; progress.update(count); } }
                    check(c);
                } finally { c.disconnect(); }
            }
            if(cancelled)throw new IOException("전송 취소됨");
            request("clipboard/uploads/"+id+"/commit","POST",new JSONObject()); committed=true;
        } finally { if(!committed)try { request("clipboard/uploads/"+id,"DELETE",null); } catch(Exception ignored) {} }
    }
    void downloadClipboard(String id,int index,OutputStream out,Progress progress) throws Exception { downloadPath("clipboard/"+URLEncoder.encode(id,"UTF-8")+"/"+index,out,1024L*1024*1024,progress); }
    void downloadClipboardImage(String id,OutputStream out,Progress progress) throws Exception { downloadPath("clipboard/"+URLEncoder.encode(id,"UTF-8")+"/image",out,16L*1024*1024,progress); }
    private void downloadPath(String path, OutputStream out, long maxBytes, Progress progress) throws Exception {
        HttpsURLConnection c = open(path, "GET");
        try {
            check(c); long total = 0; MessageDigest hash = MessageDigest.getInstance("SHA-256");
            try (InputStream in = c.getInputStream()) {
                byte[] buffer = new byte[65536]; int n;
                while ((n = in.read(buffer)) != -1) {
                    if (cancelled) throw new IOException("전송 취소됨");
                    total += n; if (total > maxBytes) throw new IOException(maxBytes <= 16L*1024*1024 ? "클립보드 이미지는 16MB 이하만 지원합니다." : "최대 파일 크기 초과");
                    hash.update(buffer,0,n); out.write(buffer,0,n); progress.update(total);
                }
            }
            if (!hex(hash.digest()).equalsIgnoreCase(c.getHeaderField("X-Content-SHA256"))) throw new IOException("파일 해시 불일치");
        } finally { c.disconnect(); }
    }
    private static String hex(byte[] bytes) { StringBuilder b = new StringBuilder(); for (byte v : bytes) b.append(String.format("%02X",v)); return b.toString(); }
}
