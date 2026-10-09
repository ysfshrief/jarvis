package app.jarvis.companion

import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import kotlin.concurrent.thread

/**
 * "Stay connected": checks JARVIS every 15 seconds so approvals reach the phone before they expire (three
 * minutes on the PC). Only runs when the user turns it on, and Android shows its permanent notification
 * the whole time. Uses a little battery; turn it off from the menu or the notification's app screen.
 */
class LiveService : Service() {
    private val handler = Handler(Looper.getMainLooper())
    private var running = false

    private val tick = object : Runnable {
        override fun run() {
            if (!running) return
            thread {
                val outcome = Poller.poll(applicationContext)
                val text = when (outcome) {
                    PollOutcome.Ok -> "Approvals and alerts arrive right away."
                    PollOutcome.Unreachable -> "Can't reach your PC right now (same Wi-Fi?)."
                    PollOutcome.WrongCertificate -> "Refusing a server that isn't your JARVIS."
                    PollOutcome.NotPaired, PollOutcome.Unpaired -> null
                }
                if (text == null) { stopSelf(); return@thread }
                getSystemService(android.app.NotificationManager::class.java).notify(Notify.LIVE_ID, Notify.live(this@LiveService, text))
                handler.postDelayed(this, 15_000)
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val n = Notify.live(this, "Connecting…")
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) startForeground(Notify.LIVE_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        else startForeground(Notify.LIVE_ID, n)
        if (!running) {
            running = true
            handler.post(tick)
        }
        return START_NOT_STICKY // after the app is killed it resumes when opened, not silently
    }

    override fun onDestroy() {
        running = false
        handler.removeCallbacksAndMessages(null)
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        fun start(ctx: Context) = ctx.startForegroundService(Intent(ctx, LiveService::class.java))
        fun stop(ctx: Context) = ctx.stopService(Intent(ctx, LiveService::class.java))
    }
}
