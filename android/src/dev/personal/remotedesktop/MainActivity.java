package dev.personal.remotedesktop;

import android.app.*;
import android.os.*;
import android.content.*;
import android.window.OnBackInvokedDispatcher;
import android.content.res.Configuration;
import android.graphics.*;
import android.graphics.drawable.GradientDrawable;
import android.view.*;
import android.view.inputmethod.EditorInfo;
import android.widget.*;
import org.json.*;
import java.io.*;
import java.util.concurrent.*;

public final class MainActivity extends Activity {
    private final ThreadPoolExecutor control = new ThreadPoolExecutor(1,1,0,TimeUnit.MILLISECONDS,new LinkedBlockingQueue<Runnable>());
    private final ExecutorService transfers = Executors.newSingleThreadExecutor();
    private final ScheduledExecutorService frames = Executors.newSingleThreadScheduledExecutor();
    private final Handler chromeHandler = new Handler(Looper.getMainLooper());
    private volatile RemoteApi remote;
    private EditText address, pin, password;
    private TextView status;
    private int statusGeneration;
    private final Runnable hideChrome = () -> { if (remote != null) setControlsOpen(false); };
    private final Runnable hideStatus = () -> {
        int token = statusGeneration;
        status.animate().alpha(0f).setDuration(180).withEndAction(() -> { if (token == statusGeneration) status.setVisibility(View.GONE); }).start();
    };
    private RemoteScreen screen;
    private Bitmap bitmap;
    private volatile boolean connecting;
    private final Object sessionGate=new Object();
    private final java.util.concurrent.atomic.AtomicBoolean recovering=new java.util.concurrent.atomic.AtomicBoolean();
    private volatile boolean resumed;
    private volatile boolean reconnectNeeded;
    private volatile boolean backgroundUncertain;
    private volatile boolean suppressToggle;
    private volatile int foregroundFailures;
    private volatile String sessionPassword="";
    private volatile String sessionHost="";
    private volatile String sessionFingerprint="";
    private long nextKeepaliveAt;
    private ClipboardFiles clipboardFiles;
    private long lastMove;
    private final java.util.concurrent.atomic.AtomicInteger generation = new java.util.concurrent.atomic.AtomicInteger();
    private volatile String selectedDisplay="";
    private String renderedDisplay="";
    private volatile JSONArray displays=new JSONArray();
    private FrameLayout root;
    private ScrollView connectionScroll;
    private LinearLayout connectionPanel;
    private LinearLayout chin;
    private Button menuButton;
    private android.content.SharedPreferences preferences;
    private Button touchModeButton;
    private boolean controlsOpen;
    private AlertDialog leaveDialog;
    private long lastBackAt;
    private int chromeGeneration;
    private int safeLeft, safeTop, safeRight, safeBottom, imeBottom;
    private int touchMode;
    private int touchSlop;
    private final Handler touchHandler=new Handler(Looper.getMainLooper());
    private int touchGeneration;
    private boolean touchActive, touchMoved, longPressTriggered;
    private float touchStartX, touchStartY, touchLastX, touchLastY;
    private float cursorX=.5f, cursorY=.5f;
    private ImageView remoteCursor;
    private Bitmap cursorGlyph;
    private volatile String cursorHash="";
    private volatile boolean cursorLocal;
    private volatile boolean cursorVisible;
    private int cursorHotX, cursorHotY;
    private final java.util.concurrent.atomic.AtomicLong cursorGeneration=new java.util.concurrent.atomic.AtomicLong();
    private volatile boolean cursorTouched;
    private float renderedViewX=0f, renderedViewY=0f, renderedViewWidth=1f, renderedViewHeight=1f;
    private static final int DIRECT_TOUCH=0, TRACKPAD=1, TRACKPAD_RIGHT_CLICK=2;
    private static final int CHROME_HIDE_MS=4500;
    private volatile boolean inputEnabled=true;
    private volatile int frameQuality=55;
    private volatile int frameRate=15;
    private volatile int pacedQuality=55;
    private volatile int paceSlow;
    private volatile int paceFast;
    private volatile int requestedQuality=55;
    private volatile double lastTransferMs=-1;
    private final Object pictureGate=new Object();
    private H264Picture h264Picture;
    private volatile RemoteApi picturedSession;
    private volatile boolean preferH264=true;
    private volatile boolean havePicture;
    private volatile boolean needKey=true;
    private volatile int h264Failures;
    private volatile String activeCodec="H.264";
    private TextView health;
    private int receivedFrames;
    private TextView secureBanner;
    private volatile String secureState = "off";
    private long healthWindow=SystemClock.elapsedRealtime();
    private long lastFrameMillis;
    private long nextFrameAt;
    private ScaleGestureDetector scaleDetector;
    private float zoom=1f, viewX=0f, viewY=0f, viewWidth=1f, viewHeight=1f;
    private boolean pinchActive;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        enableEdgeToEdge();
        scaleDetector=new ScaleGestureDetector(this,new ScaleGestureDetector.SimpleOnScaleGestureListener() {
            @Override public boolean onScaleBegin(ScaleGestureDetector detector) { pinchActive=true; cancelTouch(); return true; }
            @Override public boolean onScale(ScaleGestureDetector detector) {
                zoom=Math.max(1f,Math.min(4f,zoom*detector.getScaleFactor()));
                float focusX=detector.getFocusX()/Math.max(1f,screen==null?1f:screen.getWidth());
                float focusY=detector.getFocusY()/Math.max(1f,screen==null?1f:screen.getHeight());
                float globalX=viewX+focusX*viewWidth, globalY=viewY+focusY*viewHeight;
                viewWidth=1f/zoom; viewHeight=1f/zoom; viewX=clamp(globalX-focusX*viewWidth,0,1-viewWidth); viewY=clamp(globalY-focusY*viewHeight,0,1-viewHeight); nextFrameAt=0;
                placeCursor();
                return true;
            }
            @Override public void onScaleEnd(ScaleGestureDetector detector) { pinchActive=false; if(zoom<=1.01f)resetZoom(); }
        });
        root=new FrameLayout(this);
        root.setBackgroundColor(Color.rgb(12,16,22));
        root.setOnApplyWindowInsetsListener((v, insets) -> {
            Insets bars=insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout() | WindowInsets.Type.mandatorySystemGestures());
            Insets ime=insets.getInsets(WindowInsets.Type.ime());
            safeLeft=bars.left; safeTop=bars.top; safeRight=bars.right; safeBottom=bars.bottom; imeBottom=ime.bottom;
            applyChromeInsets();
            return WindowInsets.CONSUMED;
        });
        screen=new RemoteScreen(this,new RemoteScreen.Listener() {
            public void text(String value) { try { send(new JSONObject().put("kind","text").put("text",value)); } catch(Exception e) { report(e); } }
            public void key(int code,boolean down) { event("key",code,down); }
            public void composing(String value) { if(value.isEmpty()) showStatus("연결됨",1400); else showStatus("조합 중: "+value,-1); }
        });
        screen.setBackgroundColor(Color.rgb(20,26,36));
        screen.setScaleType(ImageView.ScaleType.FIT_CENTER);
        touchSlop=ViewConfiguration.get(this).getScaledTouchSlop();
        screen.setOnTouchListener((v,e) -> touch(e));
        screen.addOnLayoutChangeListener((view,left,top,right,bottom,oldLeft,oldTop,oldRight,oldBottom) -> placeCursor());
        root.addView(screen, new FrameLayout.LayoutParams(-1,-1));

        remoteCursor=new ImageView(this);
        remoteCursor.setScaleType(ImageView.ScaleType.FIT_XY);
        remoteCursor.setClickable(false);
        remoteCursor.setFocusable(false);
        remoteCursor.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        remoteCursor.setVisibility(View.GONE);
        root.addView(remoteCursor, new FrameLayout.LayoutParams(1, 1, Gravity.TOP | Gravity.LEFT));

        health=new TextView(this);
        health.setTextColor(Color.WHITE); health.setTextSize(11); health.setPadding(dp(8),dp(4),dp(8),dp(4));
        health.setBackground(rounded(Color.argb(150,22,26,34), dp(12)));
        health.setVisibility(View.GONE);
        root.addView(health, new FrameLayout.LayoutParams(-2,-2, Gravity.TOP | Gravity.START));

        status=new TextView(this);
        status.setTextColor(Color.WHITE); status.setTextSize(12); status.setMaxLines(3);
        status.setPadding(dp(10),dp(6),dp(10),dp(6));
        status.setBackground(rounded(Color.argb(170,22,26,34), dp(12)));
        status.setVisibility(View.GONE);
        root.addView(status, new FrameLayout.LayoutParams(-2,-2, Gravity.TOP | Gravity.START));

        secureBanner=new TextView(this);
        secureBanner.setTextColor(Color.rgb(32,36,44));
        secureBanner.setTextSize(12);
        secureBanner.setMaxLines(3);
        secureBanner.setMaxWidth(dp(520));
        secureBanner.setEllipsize(android.text.TextUtils.TruncateAt.END);
        secureBanner.setGravity(Gravity.CENTER);
        secureBanner.setPadding(dp(12),dp(8),dp(12),dp(8));
        secureBanner.setBackground(rounded(Color.argb(236,255,255,255), dp(8)));
        secureBanner.setElevation(dp(6));
        secureBanner.setClickable(false);
        secureBanner.setFocusable(false);
        secureBanner.setVisibility(View.GONE);
        FrameLayout.LayoutParams secureStart=new FrameLayout.LayoutParams(-2,-2, Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
        secureStart.leftMargin=dp(16); secureStart.rightMargin=dp(16); secureStart.bottomMargin=dp(64);
        root.addView(secureBanner, secureStart);

        connectionPanel=new LinearLayout(this);
        connectionPanel.setOrientation(LinearLayout.VERTICAL);
        connectionPanel.setPadding(dp(18),dp(16),dp(18),dp(14));
        connectionPanel.setBackground(rounded(Color.argb(246,255,255,255), dp(18)));
        TextView title=new TextView(this); title.setText("Personal Remote Desktop"); title.setTextSize(20); connectionPanel.addView(title);
        TextView hint=new TextView(this); hint.setText("서버에서 접속 대기를 켠 뒤 연결하세요. 비밀번호에서 Enter로도 접속합니다."); hint.setTextSize(12); hint.setPadding(0,dp(4),0,dp(6)); connectionPanel.addView(hint);
        address=field(connectionPanel,"서버 주소:포트 (예: 192.168.0.10:45831)",false);
        pin=field(connectionPanel,"서버 SHA-256 지문",false);
        password=field(connectionPanel,"서버 비밀번호",true);
        password.setImeOptions(EditorInfo.IME_ACTION_DONE);
        password.setOnEditorActionListener((v,actionId,event) -> {
            boolean enter=actionId==EditorInfo.IME_ACTION_DONE || actionId==EditorInfo.IME_ACTION_GO || (event!=null && event.getKeyCode()==KeyEvent.KEYCODE_ENTER && event.getAction()==KeyEvent.ACTION_DOWN);
            if(!enter) return false;
            connect();
            return true;
        });
        preferences=getPreferences(0);
        address.setText(preferences.getString("address","")); pin.setText(preferences.getString("pin",""));
        frameQuality=Math.max(25,Math.min(90,preferences.getInt("frameQuality",55)));
        frameRate=validFrameRate(preferences.getInt("frameRate",15));
        pacedQuality=frameQuality;
        touchMode=Math.max(DIRECT_TOUCH,Math.min(TRACKPAD_RIGHT_CLICK,preferences.getInt("touchMode",DIRECT_TOUCH)));
        LinearLayout actions=row(connectionPanel); button(actions,"접속", this::connect); button(actions,"종료", () -> { disconnect(); info("연결 종료됨"); });
        connectionScroll=new ScrollView(this);
        connectionScroll.setFillViewport(true);
        connectionScroll.addView(connectionPanel, new FrameLayout.LayoutParams(-1,-2));
        root.addView(connectionScroll, new FrameLayout.LayoutParams(-1,-1));

        chin=new LinearLayout(this);
        chin.setOrientation(LinearLayout.VERTICAL);
        chin.setPadding(dp(8),dp(2),dp(8),dp(8));
        chin.setBackground(rounded(Color.argb(228,248,248,248), dp(20)));
        chin.setElevation(dp(8));
        chin.setClickable(true);
        chin.setOnClickListener(v -> bumpChrome());
        chin.setVisibility(View.GONE);
        TextView grip=new TextView(this);
        grip.setText("숨기기"); grip.setGravity(Gravity.CENTER); grip.setTextSize(12); grip.setPadding(0,dp(8),0,dp(4));
        grip.setOnTouchListener((v,e) -> gripTouch(e));
        chin.addView(grip, new LinearLayout.LayoutParams(-1,-2));
        LinearLayout secondary=row(chin);
        button(secondary,"모니터", this::chooseDisplay);
        button(secondary,"키보드", () -> { if(remote!=null){ setControlsOpen(false); screen.showKeyboard(); } });
        touchModeButton=button(secondary,"", this::chooseTouchMode); updateTouchModeLabel();
        button(secondary,"화질", this::chooseQuality);
        button(secondary,"저대역폭", this::lowBandwidth);
        button(secondary,"FPS", this::chooseFrameRate);
        button(secondary,"클립보드", () -> { screen.finishComposition(); clipboardFiles.menu(); });
        button(secondary,"정보", this::showSessionInfo);
        addInputToggle(secondary);
        button(secondary,"Tab", () -> key(9));
        button(secondary,"←", () -> key(37));
        button(secondary,"→", () -> key(39));
        button(secondary,"한/영", () -> key(21));
        button(secondary,"Ctrl+Alt+Del", () -> event("attention",0,false));
        button(secondary,"접속 종료", () -> { disconnect(); info("연결 종료됨"); });
        LinearLayout primary=row(chin);
        toggle(primary,"Ctrl",17); toggle(primary,"Alt",18); toggle(primary,"Shift",16);
        button(primary,"우클릭", () -> click(1));
        button(primary,"휠 ↑", () -> event("wheel",120,false));
        button(primary,"휠 ↓", () -> event("wheel",-120,false));
        button(primary,"Enter", () -> key(13));
        button(primary,"Esc", () -> key(27));
        button(primary,"⌫", () -> key(8));
        root.addView(chin, new FrameLayout.LayoutParams(-1,-2, Gravity.BOTTOM));

        menuButton=new Button(this);
        menuButton.setText("메뉴"); menuButton.setAllCaps(false); menuButton.setTextColor(Color.WHITE); menuButton.setTextSize(13);
        menuButton.setMinWidth(0); menuButton.setMinimumWidth(0); menuButton.setMinHeight(dp(48)); menuButton.setMinimumHeight(dp(48));
        menuButton.setPadding(dp(18),0,dp(18),0);
        menuButton.setBackground(rounded(Color.argb(186,22,26,34), dp(24)));
        menuButton.setBackgroundTintList(null);
        menuButton.setStateListAnimator(null);
        menuButton.setVisibility(View.GONE);
        menuButton.setOnClickListener(v -> setControlsOpen(true));
        root.addView(menuButton, new FrameLayout.LayoutParams(-2,-2, Gravity.BOTTOM | Gravity.END));

        setContentView(root);
        root.post(this::applyChromeInsets);
        showPreviousCrash(RemoteDesktopApplication.previousCrashFile());
        setControlsOpen(false);
        registerBack();
        clipboardFiles=new ClipboardFiles(this,() -> remote,this::info,transfers);
        clipboardFiles.accept(getIntent());
        frames.scheduleWithFixedDelay(() -> {
            final RemoteApi api=remote;
            try {
                if(api==null) return;
                if(!resumed) { keepalive(api); return; }
                if(reconnectNeeded || recovering.get()) return;
                final int selectedRate=frameRate;
                long now=SystemClock.elapsedRealtime();
                if(now<nextFrameAt) return;
                nextFrameAt=now+1000L/selectedRate;
                clipboardFiles.syncText();
                final String display=selectedDisplay;
                long started=SystemClock.elapsedRealtime();
                PolledFrame polled=pollFrame(api, display, selectedRate);
                if(polled==null) return;
                lastFrameMillis=SystemClock.elapsedRealtime()-started;
                if(!polled.keep) {
                    int selectedQuality=frameQuality;
                    int[] step=LinkPace.pace(selectedQuality, requestedQuality, lastTransferMs, selectedRate, paceSlow, paceFast, true);
                    paceSlow=step[1];
                    paceFast=step[2];
                    if(step[3]==1) {
                        pacedQuality=step[0];
                        needKey=true;
                        String notice=LinkPace.notice(selectedQuality, requestedQuality, step[0]);
                        if(notice!=null) info(notice);
                    }
                }
                receivedFrames++;
                foregroundFailures=0;
                updateHealth();
                final RemoteApi.Cursor cursor=polled.cursor;
                if(polled.keep) {
                    runOnUiThread(() -> { if(remote==api && selectedDisplay.equals(display) && resumed && !reconnectNeeded) applyCursor(polled); });
                    return;
                }
                Bitmap next=polled.bitmap;
                runOnUiThread(() -> { if(remote==api && selectedDisplay.equals(display) && resumed && !reconnectNeeded) { bitmap=next; renderedDisplay=display; renderedViewX=polled.viewX; renderedViewY=polled.viewY; renderedViewWidth=polled.viewWidth; renderedViewHeight=polled.viewHeight; screen.setImageBitmap(next); applyCursor(polled); } else next.recycle(); });
            } catch(Exception e) { try { if(api!=null && remote==api) noteSessionFailure(api, e); } catch(Exception ignored) {} }
        },0,10,TimeUnit.MILLISECONDS);
    }
    private void showPreviousCrash(File previousCrashFile) {
        if(previousCrashFile==null || !previousCrashFile.isFile()) return;
        root.post(() -> {
            if(isFinishing() || !previousCrashFile.isFile()) return;
            String detail;
            try (FileInputStream input=new FileInputStream(previousCrashFile); ByteArrayOutputStream output=new ByteArrayOutputStream()) {
                byte[] buffer=new byte[2048]; int count;
                while((count=input.read(buffer))!=-1 && output.size()<12000) output.write(buffer,0,count);
                detail=output.toString("UTF-8");
            } catch(Exception error) { detail=error.toString(); }
            previousCrashFile.delete();
            final String report=detail;
            new AlertDialog.Builder(this).setTitle("이전 실행에서 앱이 종료됨")
                .setMessage(report.length()>8000?report.substring(0,8000):report)
                .setPositiveButton("오류 복사",(dialog,which) -> {
                    ((ClipboardManager)getSystemService(CLIPBOARD_SERVICE)).setPrimaryClip(ClipData.newPlainText("Remote Desktop error",report));
                    Toast.makeText(this,"오류 내용이 복사되었습니다.",Toast.LENGTH_SHORT).show();
                })
                .setNegativeButton("닫기",null).show();
        });
    }

    private void enableEdgeToEdge() {
        Window window=getWindow();
        window.setDecorFitsSystemWindows(false);
        window.setStatusBarColor(Color.TRANSPARENT);
        window.setNavigationBarColor(Color.TRANSPARENT);
        window.setStatusBarContrastEnforced(false);
        window.setNavigationBarContrastEnforced(false);
        WindowManager.LayoutParams attrs=window.getAttributes();
        attrs.layoutInDisplayCutoutMode=WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES;
        window.setAttributes(attrs);
        WindowInsetsController controller=window.getInsetsController();
        if(controller!=null) controller.setSystemBarsAppearance(0, WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS | WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS);
    }
    private GradientDrawable rounded(int color, float radius) {
        GradientDrawable drawable=new GradientDrawable();
        drawable.setColor(color);
        drawable.setCornerRadius(radius);
        return drawable;
    }
    private void applyChromeInsets() {
        if(root==null || health==null || menuButton==null || chin==null || connectionScroll==null || status==null || screen==null || secureBanner==null) return;
        int side=dp(12);
        int lower=Math.max(safeBottom, imeBottom);
        int targetWidth=-1;
        if(root.getWidth()>0) { int available=root.getWidth()-safeLeft-safeRight-dp(32); if(available>0) targetWidth=Math.min(dp(520), available); }
        FrameLayout.LayoutParams screenLp=(FrameLayout.LayoutParams)screen.getLayoutParams();
        FrameLayout.LayoutParams chinLp=(FrameLayout.LayoutParams)chin.getLayoutParams();
        FrameLayout.LayoutParams connectionLp=(FrameLayout.LayoutParams)connectionScroll.getLayoutParams();
        FrameLayout.LayoutParams secureLp=(FrameLayout.LayoutParams)secureBanner.getLayoutParams();
        int secureBottom=lower+dp(64);
        int secureSide=safeLeft+dp(16);
        if(screenLp.bottomMargin==imeBottom && chinLp.bottomMargin==lower+dp(8) && chinLp.leftMargin==safeLeft+dp(8) && connectionLp.width==targetWidth && connectionLp.topMargin==safeTop+dp(12) && secureLp.bottomMargin==secureBottom && secureLp.leftMargin==secureSide) return;
        FrameLayout.LayoutParams healthLp=(FrameLayout.LayoutParams)health.getLayoutParams();
        healthLp.leftMargin=safeLeft+side; healthLp.topMargin=safeTop+dp(8); healthLp.rightMargin=safeRight+side;
        health.setLayoutParams(healthLp);
        FrameLayout.LayoutParams statusLp=(FrameLayout.LayoutParams)status.getLayoutParams();
        statusLp.leftMargin=safeLeft+side; statusLp.topMargin=safeTop+dp(40); statusLp.rightMargin=safeRight+side;
        status.setLayoutParams(statusLp);
        connectionLp.width=targetWidth<0?-1:targetWidth;
        connectionLp.gravity=targetWidth<0?Gravity.TOP:(Gravity.TOP | Gravity.CENTER_HORIZONTAL);
        connectionLp.leftMargin=safeLeft+dp(16); connectionLp.rightMargin=safeRight+dp(16);
        connectionLp.topMargin=safeTop+dp(12); connectionLp.bottomMargin=lower+dp(12);
        connectionScroll.setLayoutParams(connectionLp);
        chinLp.leftMargin=safeLeft+dp(8); chinLp.rightMargin=safeRight+dp(8); chinLp.bottomMargin=lower+dp(8);
        chin.setLayoutParams(chinLp);
        FrameLayout.LayoutParams handleLp=(FrameLayout.LayoutParams)menuButton.getLayoutParams();
        handleLp.rightMargin=safeRight+side; handleLp.bottomMargin=lower+dp(10);
        menuButton.setLayoutParams(handleLp);
        screenLp.bottomMargin=imeBottom;
        screen.setLayoutParams(screenLp);
        secureLp.gravity=Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
        secureLp.leftMargin=secureSide;
        secureLp.rightMargin=safeRight+dp(16);
        secureLp.bottomMargin=secureBottom;
        int secureWidth=root.getWidth()-secureSide-(safeRight+dp(16));
        if(secureWidth>0) secureBanner.setMaxWidth(Math.min(dp(520), secureWidth));
        secureBanner.setLayoutParams(secureLp);
    }
    private void applySessionChrome(boolean enabled) {
        WindowInsetsController controller=getWindow().getInsetsController();
        if(controller==null) return;
        if(enabled) {
            controller.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
            controller.hide(WindowInsets.Type.systemBars());
        } else controller.show(WindowInsets.Type.systemBars());
    }
    private void setControlsOpen(boolean open) {
        if(connectionScroll.getVisibility()==View.VISIBLE) open=false;
        controlsOpen=open;
        int token=++chromeGeneration;
        chin.animate().cancel();
        menuButton.animate().cancel();
        chromeHandler.removeCallbacks(hideChrome);
        if(open) {
            menuButton.setVisibility(View.GONE);
            chin.setVisibility(View.VISIBLE);
            chin.setAlpha(0f);
            chin.setTranslationY(dp(18));
            chin.animate().alpha(1f).translationY(0f).setDuration(180).start();
            chromeHandler.postDelayed(hideChrome, CHROME_HIDE_MS);
        } else {
            if(chin.getVisibility()==View.VISIBLE) {
                chin.animate().alpha(0f).translationY(dp(18)).setDuration(150).withEndAction(() -> { if(token==chromeGeneration) chin.setVisibility(View.GONE); }).start();
            } else chin.setVisibility(View.GONE);
            boolean showHandle=remote!=null && connectionScroll.getVisibility()!=View.VISIBLE;
            menuButton.setVisibility(showHandle ? View.VISIBLE : View.GONE);
            if(showHandle) { menuButton.setAlpha(0f); menuButton.animate().alpha(1f).setDuration(120).start(); }
        }
        syncSecureBanner();
    }
    private void bumpChrome() {
        if(!controlsOpen || remote==null) return;
        chromeHandler.removeCallbacks(hideChrome);
        chromeHandler.postDelayed(hideChrome, CHROME_HIDE_MS);
    }
    private boolean gripTouch(MotionEvent e) {
        bumpChrome();
        if(e.getActionMasked()==MotionEvent.ACTION_UP) setControlsOpen(false);
        return true;
    }
    private EditText field(LinearLayout parent,String hint,boolean secret) { EditText f=new EditText(this); f.setSingleLine(); f.setTextSize(13); f.setHint(hint); f.setInputType(secret ? 129 : 1); parent.addView(f); return f; }
    private LinearLayout row(LinearLayout parent) {
        HorizontalScrollView scroll=new HorizontalScrollView(this);
        scroll.setHorizontalScrollBarEnabled(false);
        scroll.setOverScrollMode(View.OVER_SCROLL_NEVER);
        scroll.setOnTouchListener((v,e) -> { bumpChrome(); return false; });
        LinearLayout row=new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL);
        scroll.addView(row);
        parent.addView(scroll, new LinearLayout.LayoutParams(-1,-2));
        return row;
    }
    private Button button(LinearLayout row,String title,Runnable action) {
        Button b=new Button(this); b.setText(title); b.setTextSize(12); b.setAllCaps(false);
        b.setMinWidth(0); b.setMinimumWidth(0); b.setMinHeight(dp(40)); b.setMinimumHeight(dp(40));
        b.setPadding(dp(12),dp(4),dp(12),dp(4));
        LinearLayout.LayoutParams lp=new LinearLayout.LayoutParams(-2, dp(40));
        lp.setMargins(dp(3),dp(2),dp(3),dp(2));
        row.addView(b, lp);
        b.setOnClickListener(v -> { bumpChrome(); action.run(); });
        return b;
    }
    private void toggle(LinearLayout row,String title,int code) {
        ToggleButton b=new ToggleButton(this); b.setTextOn(title+" ✓"); b.setTextOff(title); b.setChecked(false); b.setTextSize(12); b.setAllCaps(false);
        b.setMinHeight(dp(40)); b.setMinimumHeight(dp(40));
        LinearLayout.LayoutParams lp=new LinearLayout.LayoutParams(-2, dp(40));
        lp.setMargins(dp(3),dp(2),dp(3),dp(2));
        row.addView(b, lp);
        b.setOnCheckedChangeListener((v,on) -> { if(suppressToggle)return; bumpChrome(); event("key",code,on); info(title+(on?" 누름":" 해제")); });
    }
    private void info(String message) { showStatus(message, 2400); }
    private void showStatus(String message, int holdMs) {
        runOnUiThread(() -> {
            statusGeneration++;
            status.animate().cancel();
            status.setText(message);
            status.setAlpha(1f);
            status.setVisibility(View.VISIBLE);
            chromeHandler.removeCallbacks(hideStatus);
            if(holdMs>=0) chromeHandler.postDelayed(hideStatus, holdMs);
        });
    }
    @Override public void onConfigurationChanged(Configuration configuration) {
        super.onConfigurationChanged(configuration);
        if(root!=null) root.requestApplyInsets();
        if(remote!=null) applySessionChrome(true);
    }
    @Override protected void onResume() {
        super.onResume();
        if(remote!=null) applySessionChrome(true);
    }
    @Override protected void onStart() {
        super.onStart();
        resumed=true;
        foregroundFailures=0;
        nextFrameAt=0;
        RemoteApi api=remote;
        if(api==null) return;
        applySessionChrome(true);
        if(reconnectNeeded || backgroundUncertain) {
            showStatus("다시 연결 중", -1);
            health.setVisibility(View.GONE);
            recover(api, reconnectNeeded);
        }
    }
    @Override protected void onStop() {
        resumed=false;
        nextKeepaliveAt=0;
        if(remote!=null) {
            cancelTouch();
            suppressToggle=true;
            uncheckModifiers(chin);
            suppressToggle=false;
            event("release",0,false);
            health.setVisibility(View.GONE);
        }
        super.onStop();
    }
    private int dp(int value) { return Math.round(value*getResources().getDisplayMetrics().density); }
    private void report(Exception e) { showStatus(e.getMessage()==null ? e.toString() : e.getMessage(), 5000); }
    private void connect() {
        if(remote!=null || connecting || recovering.get()) return;
        connecting=true;
        final String host=address.getText().toString(), fingerprint=pin.getText().toString(), pass=password.getText().toString();
        info("접속 중…");
        final int attempt=generation.get();
        control.execute(() -> {
            RemoteApi api=null;
            try {
                api=new RemoteApi(host,fingerprint);
                api.login(pass);
                JSONArray list=new JSONArray(new String(api.request("displays","GET",null),"UTF-8"));
                String wanted=preferences.getString("address","").equals(host)?preferences.getString("display",""):"";
                String display=defaultDisplay(list,wanted);
                boolean accepted=false;
                synchronized(sessionGate) {
                    if(attempt==generation.get()) {
                        displays=list;
                        selectedDisplay=display;
                        sessionHost=host;
                        sessionFingerprint=fingerprint;
                        sessionPassword=pass;
                        cursorTouched=false;
                        reconnectNeeded=false;
                        backgroundUncertain=false;
                        foregroundFailures=0;
                        nextFrameAt=0;
                        nextKeepaliveAt=SystemClock.elapsedRealtime()+5000L;
                        remote=api;
                        api=null;
                        accepted=true;
                    }
                }
                if(!accepted) { try { api.logout(); } catch(Exception ignored) {} return; }
                runOnUiThread(() -> {
                    if(generation.get()!=attempt || remote==null) return;
                    password.setText("");
                    connectionScroll.setVisibility(View.GONE);
                    applySessionChrome(true);
                    setControlsOpen(false);
                    preferences.edit().putString("address",host).putString("pin",fingerprint).putString("display",selectedDisplay).apply();
                });
                info("연결됨 · "+touchModeDescription());
            } catch(Exception e) {
                if(api!=null) try { api.logout(); } catch(Exception ignored) {}
                report(e);
            } finally { connecting=false; }
        });
    }
    private void disconnect() {
        cancelTouch();
        RemoteApi api;
        synchronized(sessionGate) {
            generation.incrementAndGet();
            api=remote;
            remote=null;
            sessionPassword="";
            sessionHost="";
            sessionFingerprint="";
            reconnectNeeded=false;
            backgroundUncertain=false;
            foregroundFailures=0;
            havePicture=false;
            needKey=true;
            h264Failures=0;
            preferH264=true;
            activeCodec="H.264";
            picturedSession=null;
            pacedQuality=frameQuality;
            paceSlow=0;
            paceFast=0;
            secureState="off";
            cursorHash="";
            cursorLocal=false;
            cursorVisible=false;
            cursorGeneration.incrementAndGet();
            cursorTouched=false;
        }
        closePicture();
        if(api!=null) { api.cancelled=true; if(!control.isShutdown()) control.execute(api::logout); }
        runOnUiThread(() -> {
            connectionScroll.setVisibility(View.VISIBLE);
            applySessionChrome(false);
            setControlsOpen(false);
            health.setVisibility(View.GONE);
            if(secureBanner!=null) secureBanner.setVisibility(View.GONE);
            screen.resetInput();
            screen.setImageDrawable(null);
            bitmap=null;
            if(remoteCursor!=null) { remoteCursor.setImageDrawable(null); remoteCursor.setVisibility(View.GONE); }
            if(cursorGlyph!=null) { cursorGlyph.recycle(); cursorGlyph=null; }
            android.view.inputmethod.InputMethodManager imm=(android.view.inputmethod.InputMethodManager)getSystemService(INPUT_METHOD_SERVICE);
            if(screen.getWindowToken()!=null) imm.hideSoftInputFromWindow(screen.getWindowToken(),0);
        });
    }
    private void send(JSONObject event) {
        RemoteApi api=remote;
        if(api==null || control.isShutdown() || (!inputEnabled && !"release".equals(event.optString("kind")))) return;
        if(control.getQueue().size()>128) { info("입력 지연으로 연결 종료"); disconnect(); return; }
        control.execute(() -> { if(remote!=api) return; try { String notice=api.input(event); if(notice!=null) info(notice); } catch(Exception e) { noteSessionFailure(api, e); } });
    }
    private void keepalive(RemoteApi api) {
        if(recovering.get() || reconnectNeeded) return;
        long now=SystemClock.elapsedRealtime();
        if(now<nextKeepaliveAt) return;
        nextKeepaliveAt=now+5000L;
        try {
            api.ping();
            if(remote!=api) return;
            backgroundUncertain=false;
            foregroundFailures=0;
        } catch(Exception e) {
            if(remote!=api) return;
            backgroundUncertain=true;
            if(statusCode(e)==401) { reconnectNeeded=true; recover(api, true); }
        }
    }
    private void noteSessionFailure(RemoteApi api, Exception error) {
        if(remote!=api) return;
        if(statusCode(error)==401) { reconnectNeeded=true; recover(api, true); return; }
        if(!resumed) { backgroundUncertain=true; return; }
        if(++foregroundFailures<3) return;
        reconnectNeeded=true;
        recover(api, true);
    }
    private void recover(RemoteApi failed, boolean forceLogin) {
        if(failed==null || sessionPassword.isEmpty()) return;
        if(!recovering.compareAndSet(false, true)) return;
        final int attempt=generation.get();
        if(forceLogin) {
            showStatus("다시 연결 중", -1);
            runOnUiThread(() -> health.setVisibility(View.GONE));
        }
        try { control.execute(() -> {
            RemoteApi fresh=null;
            try {
                if(!sameSession(attempt, failed)) return;
                if(!forceLogin) {
                    try {
                        failed.ping();
                        if(!sameSession(attempt, failed)) return;
                        backgroundUncertain=false;
                        reconnectNeeded=false;
                        foregroundFailures=0;
                        nextFrameAt=0;
                        showStatus("연결됨", 1200);
                        return;
                    } catch(Exception error) {
                        if(!sameSession(attempt, failed)) return;
                        if(statusCode(error)!=401 && !resumed) { backgroundUncertain=true; return; }
                        reconnectNeeded=true;
                        showStatus("다시 연결 중", -1);
                        runOnUiThread(() -> health.setVisibility(View.GONE));
                    }
                }
                String host=sessionHost, fingerprint=sessionFingerprint, pass=sessionPassword;
                if(pass.isEmpty() || !sameSession(attempt, failed)) return;
                fresh=new RemoteApi(host, fingerprint);
                fresh.login(pass);
                JSONArray list=new JSONArray(new String(fresh.request("displays","GET",null),"UTF-8"));
                String display=defaultDisplay(list, selectedDisplay);
                boolean accepted=false;
                RemoteApi previous=null;
                synchronized(sessionGate) {
                    if(attempt==generation.get()) {
                        previous=remote;
                        displays=list;
                        selectedDisplay=display;
                        sessionHost=host;
                        sessionFingerprint=fingerprint;
                        sessionPassword=pass;
                        remote=fresh;
                        reconnectNeeded=false;
                        backgroundUncertain=false;
                        foregroundFailures=0;
                        nextFrameAt=0;
                        nextKeepaliveAt=SystemClock.elapsedRealtime()+5000L;
                        fresh=null;
                        accepted=true;
                    }
                }
                if(!accepted) { fresh.logout(); return; }
                runOnUiThread(() -> {
                    if(generation.get()!=attempt || remote==null) return;
                    connectionScroll.setVisibility(View.GONE);
                    if(resumed) applySessionChrome(true);
                });
                showStatus("다시 연결됨", 1400);
                if(previous!=null && previous!=remote) { previous.cancelled=true; try { previous.logout(); } catch(Exception ignored) {} }
            } catch(Exception error) {
                if(fresh!=null) try { fresh.logout(); } catch(Exception ignored) {}
                if(!sameSession(attempt, failed)) return;
                if(statusCode(error)==409) {
                    try {
                        failed.ping();
                        reconnectNeeded=false;
                        backgroundUncertain=false;
                        foregroundFailures=0;
                        nextFrameAt=0;
                        showStatus("연결됨", 1200);
                        return;
                    } catch(Exception pingError) {
                        if(statusCode(pingError)!=401) error=pingError;
                    }
                }
                reconnectNeeded=true;
                backgroundUncertain=true;
                report(error);
                chromeHandler.postDelayed(() -> {
                    if(generation.get()==attempt && remote==failed && !sessionPassword.isEmpty()) recover(failed, true);
                }, resumed ? 3000L : 5000L);
            } finally { recovering.set(false); }
        });
        } catch(RuntimeException rejected) { recovering.set(false); if(remote==failed) report(rejected); }
    }
    private boolean sameSession(int attempt, RemoteApi api) {
        synchronized(sessionGate) { return attempt==generation.get() && remote==api; }
    }
    private static int statusCode(Exception error) {
        return error instanceof RemoteApi.StatusException ? ((RemoteApi.StatusException)error).code : 0;
    }
    private void uncheckModifiers(View view) {
        if(view instanceof ToggleButton) {
            ToggleButton toggle=(ToggleButton)view;
            CharSequence text=toggle.getText();
            String label=text==null ? "" : text.toString();
            if(label.startsWith("Ctrl") || label.startsWith("Alt") || label.startsWith("Shift")) toggle.setChecked(false);
            return;
        }
        if(view instanceof ViewGroup) {
            ViewGroup group=(ViewGroup)view;
            for(int i=0;i<group.getChildCount();i++) uncheckModifiers(group.getChildAt(i));
        }
    }
    private void event(String kind,int code,boolean down) { try { send(new JSONObject().put("kind",kind).put("code",code).put("down",down)); } catch(Exception e) { report(e); } }
    private void key(int code) { screen.finishComposition(); event("key",code,true); event("key",code,false); }
    private void click(int code) { event("button",code,true); event("button",code,false); }
    private boolean touch(MotionEvent e) {
        if(remote==null || bitmap==null)return true;
        if(controlsOpen && e.getActionMasked()==MotionEvent.ACTION_DOWN) { setControlsOpen(false); return true; }
        scaleDetector.onTouchEvent(e);
        if(pinchActive || e.getPointerCount()>1)return true;
        int action=e.getActionMasked();
        if(action==MotionEvent.ACTION_POINTER_DOWN || action==MotionEvent.ACTION_POINTER_UP) { cancelTouch(); return true; }
        if(action==MotionEvent.ACTION_CANCEL) { cancelTouch(); return true; }
        if(action==MotionEvent.ACTION_DOWN) {
            screen.finishComposition(); screen.requestFocus(); beginTouch(e); return true;
        }
        if(!touchActive || (action!=MotionEvent.ACTION_MOVE && action!=MotionEvent.ACTION_UP))return true;
        if(zoom>1f && action==MotionEvent.ACTION_MOVE) {
            float dx=e.getX()-touchStartX, dy=e.getY()-touchStartY;
            if(!touchMoved && dx*dx+dy*dy>touchSlop*touchSlop) {
                touchMoved=true; touchHandler.removeCallbacksAndMessages(null);
                if(touchMode==DIRECT_TOUCH) event("button",0,false);
            }
            if(touchMoved) { panViewport(e.getX()-touchLastX,e.getY()-touchLastY); touchLastX=e.getX(); touchLastY=e.getY(); return true; }
        }
        if(touchMode==DIRECT_TOUCH) return directTouch(e,action);
        return trackpadTouch(e,action);
    }
    private void beginTouch(MotionEvent e) {
        touchGeneration++; touchActive=true; touchMoved=false; longPressTriggered=false;
        touchStartX=touchLastX=e.getX(); touchStartY=touchLastY=e.getY();
        if(touchMode==DIRECT_TOUCH) {
            float[] position=absolutePosition(e); if(position==null) { touchActive=false; return; }
            moveCursor(position[0],position[1]); event("button",0,true);
        } else if(touchMode==TRACKPAD_RIGHT_CLICK) {
            final int touchToken=touchGeneration;
            touchHandler.postDelayed(() -> {
                if(touchActive && !touchMoved && !longPressTriggered && touchToken==touchGeneration && touchMode==TRACKPAD_RIGHT_CLICK) {
                    longPressTriggered=true; screen.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS); click(1); info("우클릭");
                }
            },ViewConfiguration.getLongPressTimeout());
        }
    }
    private boolean directTouch(MotionEvent e,int action) {
        if(action==MotionEvent.ACTION_MOVE && SystemClock.elapsedRealtime()-lastMove<25)return true;
        float[] position=absolutePosition(e); if(position!=null)moveCursor(position[0],position[1]);
        if(action==MotionEvent.ACTION_UP) { if(!(zoom>1f && touchMoved))event("button",0,false); finishTouch(); }
        else lastMove=SystemClock.elapsedRealtime();
        return true;
    }
    private boolean trackpadTouch(MotionEvent e,int action) {
        float dx=e.getX()-touchStartX,dy=e.getY()-touchStartY;
        if(!touchMoved && dx*dx+dy*dy>touchSlop*touchSlop) { touchMoved=true; touchHandler.removeCallbacksAndMessages(null); }
        if(action==MotionEvent.ACTION_MOVE) {
            if(SystemClock.elapsedRealtime()-lastMove<20)return true;
            float width=Math.max(1,screen.getWidth()),height=Math.max(1,screen.getHeight());
            moveCursor(cursorX+(e.getX()-touchLastX)/width*renderedViewWidth*1.25f,cursorY+(e.getY()-touchLastY)/height*renderedViewHeight*1.25f);
            touchLastX=e.getX(); touchLastY=e.getY(); lastMove=SystemClock.elapsedRealtime(); return true;
        }
        if(!touchMoved && !longPressTriggered)click(0);
        finishTouch(); return true;
    }
    private float[] absolutePosition(MotionEvent e) {
        float scale=Math.min((float)screen.getWidth()/bitmap.getWidth(),(float)screen.getHeight()/bitmap.getHeight());
        float width=bitmap.getWidth()*scale,height=bitmap.getHeight()*scale;
        float x=(e.getX()-(screen.getWidth()-width)/2)/width,y=(e.getY()-(screen.getHeight()-height)/2)/height;
        if(x<0 || x>1 || y<0 || y>1)return null;
        return new float[]{renderedViewX+x*renderedViewWidth,renderedViewY+y*renderedViewHeight};
    }
    private void panViewport(float dx,float dy) {
        float width=Math.max(1,screen.getWidth()), height=Math.max(1,screen.getHeight());
        viewX=clamp(viewX-dx/width*viewWidth,0,1-viewWidth);
        viewY=clamp(viewY-dy/height*viewHeight,0,1-viewHeight);
        nextFrameAt=0;
        placeCursor();
    }
    private void moveCursor(float x,float y) {
        cursorGeneration.incrementAndGet();
        cursorTouched=true;
        cursorX=Math.max(0,Math.min(1,x)); cursorY=Math.max(0,Math.min(1,y));
        try { send(new JSONObject().put("kind","move").put("display",renderedDisplay).put("x",cursorX).put("y",cursorY)); } catch(Exception e) { report(e); }
        placeCursor();
    }
    private void finishTouch() { touchActive=false; touchHandler.removeCallbacksAndMessages(null); }
    private void cancelTouch() { touchGeneration++; if(touchActive && touchMode==DIRECT_TOUCH)event("button",0,false); touchActive=false; touchHandler.removeCallbacksAndMessages(null); event("release",0,false); }
    private String touchModeDescription() { return touchMode==DIRECT_TOUCH?"직접 터치":touchMode==TRACKPAD?"터치패드: 드래그 이동·탭 클릭":"터치패드: 길게 눌러 우클릭"; }
    private void updateTouchModeLabel() { if(touchModeButton!=null)touchModeButton.setText(touchMode==DIRECT_TOUCH?"터치: 직접":touchMode==TRACKPAD?"터치: 터치패드":"터치: 길게 우클릭"); }
    private void chooseTouchMode() {
        String[] modes={"직접 터치 — 누른 위치를 바로 클릭, 드래그는 좌클릭 드래그","터치패드 — 드래그로 커서 이동, 탭으로 좌클릭","터치패드 + 길게 누르기 — 드래그로 커서 이동, 탭은 좌클릭, 길게 누르면 우클릭"};
        new AlertDialog.Builder(this).setTitle("모바일 터치 방식").setSingleChoiceItems(modes,touchMode,(dialog,which) -> {
            cancelTouch(); touchMode=which; preferences.edit().putInt("touchMode",touchMode).apply(); updateTouchModeLabel(); dialog.dismiss(); info("터치 방식: "+touchModeDescription());
        }).setNegativeButton("취소",null).show();
    }
    private void addInputToggle(LinearLayout toolbar) {
        ToggleButton controlButton=new ToggleButton(this); controlButton.setTextOn("입력: 켜짐"); controlButton.setTextOff("입력: 꺼짐"); controlButton.setChecked(true); controlButton.setTextSize(12); controlButton.setAllCaps(false);
        controlButton.setMinHeight(dp(40)); controlButton.setMinimumHeight(dp(40));
        LinearLayout.LayoutParams lp=new LinearLayout.LayoutParams(-2, dp(40));
        lp.setMargins(dp(3),dp(2),dp(3),dp(2));
        toolbar.addView(controlButton, lp);
        controlButton.setOnCheckedChangeListener((view,checked) -> { bumpChrome(); if(!checked)event("release",0,false); inputEnabled=checked; cursorGeneration.incrementAndGet(); cursorTouched=false; nextFrameAt=0; placeCursor(); if(remote!=null)info(checked?"입력 제어 켜짐":"입력 제어 꺼짐 · 화면 보기만 가능"); });
    }
    private void chooseQuality() {
        String[] labels=qualityLabels(); int[] values={30,55,75}; int selected=frameQuality<=40?0:frameQuality>=65?2:1;
        new AlertDialog.Builder(this).setTitle("화면 품질").setSingleChoiceItems(labels,selected,(dialog,which) -> { frameQuality=values[which]; pacedQuality=frameQuality; paceSlow=0; paceFast=0; needKey=true; preferences.edit().putInt("frameQuality",frameQuality).apply(); dialog.dismiss(); info("화면 품질: "+qualityLabels()[which]); }).setNegativeButton("취소",null).show();
    }
    private void lowBandwidth() { frameQuality=25; frameRate=5; pacedQuality=25; paceSlow=0; paceFast=0; needKey=true; preferences.edit().putInt("frameQuality",25).putInt("frameRate",5).apply(); info(preferH264 && "H.264".equals(activeCodec)?"저대역폭 모드: 품질 25 · 5 FPS":"저대역폭 모드: JPEG 25 · 5 FPS"); }
    private static float clamp(float value,float min,float max) { return Math.max(min,Math.min(max,value)); }
    private void resetZoom() { zoom=1f; viewX=viewY=0f; viewWidth=viewHeight=1f; nextFrameAt=0; placeCursor(); info("확대 초기화"); }
    private void chooseFrameRate() {
        String[] labels={"5 FPS · 저전력","10 FPS · 절약","15 FPS · 균형","30 FPS · 최대"}; int[] values={5,10,15,30}; int selected=frameRate==5?0:frameRate==10?1:frameRate==30?3:2;
        new AlertDialog.Builder(this).setTitle("최대 프레임 속도").setSingleChoiceItems(labels,selected,(dialog,which) -> { frameRate=values[which]; paceSlow=0; paceFast=0; nextFrameAt=0; needKey=true; preferences.edit().putInt("frameRate",frameRate).apply(); dialog.dismiss(); info("최대 프레임 속도: "+frameRate+" FPS"); }).setNegativeButton("취소",null).show();
    }
    private static int validFrameRate(int rate) { return rate==5 || rate==10 || rate==15 || rate==30 ? rate : 15; }
    private void showSessionInfo() {
        String resolution=bitmap==null?"수신 전":bitmap.getWidth()+"×"+bitmap.getHeight();
        String nl=String.valueOf((char)10);
        int asked=Math.max(25,Math.min(pacedQuality,frameQuality));
        String paced=asked==frameQuality?"요청과 같음":Integer.toString(asked);
        String secureShown=SecureNotice.text(secureState);
        if(secureShown==null) secureShown="일반 화면";
        String pointerShown=inputEnabled?(cursorLocal?"화면 위에 따로":"확인 중"):"영상에 포함";
        new AlertDialog.Builder(this).setTitle("세션 정보").setMessage("대상: "+address.getText()+nl+"모니터: "+renderedDisplay+nl+"수신 해상도: "+resolution+nl+"코덱: "+activeCodec+nl+"품질: "+frameQuality+nl+"전송 화질: "+paced+nl+"화면: "+secureShown+nl+"포인터: "+pointerShown+nl+"최대 프레임 속도: "+frameRate+" FPS"+nl+"입력 제어: "+(inputEnabled?"켜짐":"꺼짐")+nl+"최근 프레임: "+lastFrameMillis+" ms").setPositiveButton("닫기",null).show();
    }
    private void updateHealth() {
        long now=SystemClock.elapsedRealtime(); if(now-healthWindow<1000)return;
        final float fps=receivedFrames*1000f/(now-healthWindow); receivedFrames=0; healthWindow=now;
        runOnUiThread(() -> { if(remote==null || !resumed || reconnectNeeded) return; health.setVisibility(View.VISIBLE); health.setText(String.format(java.util.Locale.US,"%.0f fps · %d ms · %s",fps,lastFrameMillis,activeCodec)); });
    }
    @Override protected void onActivityResult(int request,int result,Intent data) {
        super.onActivityResult(request,result,data);
        if(request==7 && result==RESULT_OK && data!=null && data.getData()!=null)clipboardFiles.pasteFolder(data.getData());
    }
    @Override protected void onNewIntent(Intent intent) { super.onNewIntent(intent); setIntent(intent); clipboardFiles.accept(intent); }
    private static String defaultDisplay(JSONArray list,String wanted) throws Exception {
        String primary="all";
        for(int i=0;i<list.length();i++) { JSONObject d=list.getJSONObject(i); if(d.getString("id").equals(wanted))return wanted; if(d.getBoolean("primary"))primary=d.getString("id"); }
        return primary;
    }
    private void chooseDisplay() {
        RemoteApi api=remote; if(api==null)return; screen.finishComposition();
        transfers.execute(() -> { try {
            JSONArray list=new JSONArray(new String(api.request("displays","GET",null),"UTF-8"));
            String[] names=new String[list.length()],ids=new String[list.length()];
            for(int i=0;i<list.length();i++) { JSONObject d=list.getJSONObject(i); ids[i]=d.getString("id"); names[i]=d.getString("name")+" · "+d.getInt("width")+"×"+d.getInt("height"); }
            runOnUiThread(() -> { if(remote!=api)return; new AlertDialog.Builder(this).setTitle("모니터 선택").setItems(names,(dialog,index) -> {
                if(remote!=api)return; cancelTouch(); bitmap=null; screen.setImageDrawable(null); selectedDisplay=ids[index]; havePicture=false; needKey=true; zoom=1f; viewX=viewY=0f; viewWidth=viewHeight=1f; renderedViewX=renderedViewY=0f; renderedViewWidth=renderedViewHeight=1f; cursorTouched=false; if(remoteCursor!=null)remoteCursor.setVisibility(View.GONE); preferences.edit().putString("display",selectedDisplay).apply();
            }).setNegativeButton("닫기",null).show(); });
        } catch(Exception e) { report(e); } });
    }
    private void registerBack() {
        if (Build.VERSION.SDK_INT < 33) return;
        getOnBackInvokedDispatcher().registerOnBackInvokedCallback(OnBackInvokedDispatcher.PRIORITY_DEFAULT, this::handleBack);
    }
    @Override public void onBackPressed() { handleBack(); }
    private void handleBack() {
        long now = SystemClock.elapsedRealtime();
        if (now - lastBackAt < 100) return;
        lastBackAt = now;
        if (leaveDialog != null && leaveDialog.isShowing()) { leaveDialog.dismiss(); return; }
        if (controlsOpen) { setControlsOpen(false); return; }
        if (SessionBack.ask(false, remote != null)) { confirmDisconnect(); return; }
        finish();
    }
    private void confirmDisconnect() {
        if (isFinishing()) return;
        String shown = address == null ? "" : address.getText().toString();
        leaveDialog = new AlertDialog.Builder(this)
            .setMessage(SessionBack.prompt(shown))
            .setPositiveButton("연결 끊기", (dialog, which) -> { disconnect(); info("연결 종료됨"); })
            .setNegativeButton("계속", null)
            .create();
        leaveDialog.show();
    }
    @Override public void onWindowFocusChanged(boolean focus) { super.onWindowFocusChanged(focus); if(!focus) cancelTouch(); else if(remote!=null) applySessionChrome(true); }
    @Override protected void onDestroy() { disconnect(); clipboardFiles.dispose(); frames.shutdownNow(); transfers.shutdownNow(); control.shutdown(); super.onDestroy(); }
    private PolledFrame pollFrame(RemoteApi api, String display, int requestedFps) throws Exception {
        final int epoch=generation.get();
        if(picturedSession!=api) { picturedSession=api; needKey=true; }
        final float frameViewX=viewX, frameViewY=viewY, frameViewWidth=viewWidth, frameViewHeight=viewHeight;
        final long frameCursorGeneration=cursorGeneration.get();
        String view=String.format(java.util.Locale.US,"%.5f,%.5f,%.5f,%.5f",frameViewX,frameViewY,frameViewWidth,frameViewHeight);
        boolean video=preferH264;
        requestedQuality=Math.max(25,Math.min(pacedQuality,frameQuality));
        String cursorQuery="";
        if(inputEnabled) {
            cursorQuery="&cursor=local";
            if(cursorHash!=null && cursorHash.length()>0) cursorQuery+="&shape="+java.net.URLEncoder.encode(cursorHash,"UTF-8");
        }
        String path="frame?display="+java.net.URLEncoder.encode(display,"UTF-8")+"&quality="+requestedQuality+"&fps="+requestedFps+"&view="+view+cursorQuery+(havePicture?"&cached=1":"")+(video?"&codec=h264"+(needKey?"&key=1":""):"");
        RemoteApi.Picture picture=api.frame(path);
        lastTransferMs=picture.transferMs;
        noteSecure(api, epoch, picture.secure);
        if(picture.bytes.length==0) return havePicture?new PolledFrame(null,true,picture.cursor,frameViewX,frameViewY,frameViewWidth,frameViewHeight,frameCursorGeneration):null;
        if(video && "h264".equalsIgnoreCase(picture.codec)) {
            Bitmap decoded;
            H264Picture decoder=null;
            try {
                synchronized(pictureGate) {
                    if(generation.get()!=epoch || remote!=api || !display.equals(selectedDisplay)) return null;
                    if(h264Picture==null) h264Picture=new H264Picture();
                    decoder=h264Picture;
                    decoded=decoder.decode(picture.bytes,requestedFps);
                }
                synchronized(sessionGate) {
                    if(generation.get()!=epoch || remote!=api || !display.equals(selectedDisplay)) { decoded.recycle(); return null; }
                    h264Failures=0;
                    activeCodec="H.264";
                    needKey=false;
                    havePicture=true;
                }
                return new PolledFrame(decoded,false,picture.cursor,frameViewX,frameViewY,frameViewWidth,frameViewHeight,frameCursorGeneration);
            } catch(Exception error) {
                boolean current;
                synchronized(sessionGate) {
                    current=generation.get()==epoch && remote==api;
                    if(!current) return null;
                    needKey=true;
                    if(++h264Failures>=8) {
                        preferH264=false;
                        activeCodec="JPEG";
                        info("H.264를 사용할 수 없어 JPEG로 표시합니다.");
                    }
                }
                synchronized(pictureGate) {
                    if(decoder!=null && h264Picture==decoder) { decoder.close(); h264Picture=null; }
                }
                return null;
            }
        }
        Bitmap jpeg=BitmapFactory.decodeByteArray(picture.bytes,0,picture.bytes.length);
        if(jpeg==null) throw new IOException("화면 디코딩 실패");
        synchronized(sessionGate) {
            if(generation.get()!=epoch || remote!=api || !display.equals(selectedDisplay)) { jpeg.recycle(); return null; }
            activeCodec="JPEG";
            needKey=false;
            havePicture=true;
        }
        return new PolledFrame(jpeg,false,picture.cursor,frameViewX,frameViewY,frameViewWidth,frameViewHeight,frameCursorGeneration);
    }
    private void closePicture() {
        synchronized(pictureGate) {
            if(h264Picture!=null) { h264Picture.close(); h264Picture=null; }
        }
    }
    private void noteSecure(RemoteApi api, int epoch, String header) {
        String next=SecureNotice.state(header);
        synchronized(sessionGate) {
            if(generation.get()!=epoch || remote!=api) return;
            if(next.equals(secureState)) return;
            secureState=next;
        }
        runOnUiThread(() -> { if(generation.get()!=epoch || remote!=api) return; syncSecureBanner(); });
    }
    private void syncSecureBanner() {
        if(secureBanner==null) return;
        String message=SecureNotice.text(secureState);
        if(remote==null || !SecureNotice.visible(controlsOpen, message)) {
            secureBanner.setVisibility(View.GONE);
            return;
        }
        secureBanner.setText(message);
        secureBanner.setVisibility(View.VISIBLE);
    }
    private void applyCursor(RemoteApi.Cursor cursor) {
        if(cursor==null || remoteCursor==null) return;
        cursorLocal=cursor.local;
        cursorVisible=cursor.visible;
        cursorHotX=cursor.hotX;
        cursorHotY=cursor.hotY;
        if(cursor.png!=null && cursor.png.length>8 && cursor.hash.length()>0 && !cursor.hash.equals(cursorHash)) {
            BitmapFactory.Options bounds=new BitmapFactory.Options();
            bounds.inJustDecodeBounds=true;
            BitmapFactory.decodeByteArray(cursor.png,0,cursor.png.length,bounds);
            Bitmap decoded=(bounds.outWidth>0 && bounds.outWidth<=256 && bounds.outHeight>0 && bounds.outHeight<=256)?BitmapFactory.decodeByteArray(cursor.png,0,cursor.png.length):null;
            if(decoded!=null) {
                Bitmap previous=cursorGlyph;
                cursorGlyph=decoded;
                cursorHash=cursor.hash;
                remoteCursor.setImageBitmap(decoded);
                if(previous!=null) previous.recycle();
            }
        }
        placeCursor();
    }
    private void applyCursor(PolledFrame frame) {
        if(frame==null || frame.cursor==null) return;
        if(inputEnabled && frame.cursor.local && frame.cursor.hasPosition && !cursorTouched && cursorGeneration.get()==frame.cursorGeneration) {
            cursorX=clamp((float)(frame.viewX+frame.cursor.x*frame.viewWidth),0,1);
            cursorY=clamp((float)(frame.viewY+frame.cursor.y*frame.viewHeight),0,1);
        }
        applyCursor(frame.cursor);
    }
    private void placeCursor() {
        if(remoteCursor==null) return;
        if(remote==null || !inputEnabled || !cursorLocal || !cursorVisible || cursorGlyph==null || bitmap==null || screen==null) {
            remoteCursor.setVisibility(View.GONE);
            return;
        }
        float[] norm=LocalCursor.inView(cursorX,cursorY,renderedViewX,renderedViewY,renderedViewWidth,renderedViewHeight);
        if(norm==null) { remoteCursor.setVisibility(View.GONE); return; }
        LocalCursor.Mark mark=LocalCursor.place(norm[0],norm[1],cursorHotX,cursorHotY,cursorGlyph.getWidth(),cursorGlyph.getHeight(),bitmap.getWidth(),bitmap.getHeight(),screen.getWidth(),screen.getHeight());
        if(!mark.shown) { remoteCursor.setVisibility(View.GONE); return; }
        FrameLayout.LayoutParams lp=(FrameLayout.LayoutParams)remoteCursor.getLayoutParams();
        lp.gravity=Gravity.TOP | Gravity.LEFT;
        lp.leftMargin=Math.round(mark.left);
        lp.topMargin=Math.round(mark.top);
        lp.width=Math.max(1, Math.round(mark.width));
        lp.height=Math.max(1, Math.round(mark.height));
        remoteCursor.setLayoutParams(lp);
        remoteCursor.setVisibility(View.VISIBLE);
    }
    private String[] qualityLabels() {
        if(preferH264 && "H.264".equals(activeCodec)) return new String[]{"저대역폭 · 30","균형 · 55","고화질 · 75"};
        return new String[]{"저대역폭 · JPEG 30","균형 · JPEG 55","고화질 · JPEG 75"};
    }
    private static final class PolledFrame {
        final Bitmap bitmap;
        final boolean keep;
        final RemoteApi.Cursor cursor;
        final float viewX, viewY, viewWidth, viewHeight;
        final long cursorGeneration;
        PolledFrame(Bitmap bitmap, boolean keep, RemoteApi.Cursor cursor, float viewX, float viewY, float viewWidth, float viewHeight, long cursorGeneration) { this.bitmap=bitmap; this.keep=keep; this.cursor=cursor; this.viewX=viewX; this.viewY=viewY; this.viewWidth=viewWidth; this.viewHeight=viewHeight; this.cursorGeneration=cursorGeneration; }
    }
}
