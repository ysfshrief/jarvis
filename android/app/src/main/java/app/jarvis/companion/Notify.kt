package app.jarvis.companion

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.graphics.drawable.Icon
import android.os.Build

object Notify {
    const val APPROVALS = "approvals"
    const val ALERTS = "alerts"
    const val LIVE = "live"
    const val LIVE_ID = 1

    fun channels(ctx: Context) {
        val nm = ctx.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(APPROVALS, ctx.getString(R.string.channel_approvals), NotificationManager.IMPORTANCE_HIGH).apply {
            lockscreenVisibility = Notification.VISIBILITY_PRIVATE
        })
        nm.createNotificationChannel(NotificationChannel(ALERTS, ctx.getString(R.string.channel_alerts), NotificationManager.IMPORTANCE_DEFAULT).apply {
            lockscreenVisibility = Notification.VISIBILITY_PRIVATE
        })
        nm.createNotificationChannel(NotificationChannel(LIVE, ctx.getString(R.string.channel_live), NotificationManager.IMPORTANCE_MIN))
    }

    private fun openApp(ctx: Context): PendingIntent =
        PendingIntent.getActivity(ctx, 0, Intent(ctx, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP), PendingIntent.FLAG_IMMUTABLE)

    fun idOf(key: String) = key.hashCode() and 0x7fffffff or 0x100

    /** An approval request from the PC, with Approve/Refuse buttons that need the phone to be unlocked. */
    fun approval(ctx: Context, id: String, summary: String, risk: String, reason: String) {
        fun action(approve: Boolean, label: Int): Notification.Action {
            val intent = Intent(ctx, ApprovalReceiver::class.java).putExtra("id", id).putExtra("approve", approve)
            val pi = PendingIntent.getBroadcast(ctx, idOf(id + approve), intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
            val b = Notification.Action.Builder(Icon.createWithResource(ctx, R.drawable.ic_orb), ctx.getString(label), pi)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) b.setAuthenticationRequired(true)
            return b.build()
        }
        val hidden = Notification.Builder(ctx, APPROVALS).setSmallIcon(R.drawable.ic_orb).setContentTitle("JARVIS needs your approval").build()
        val n = Notification.Builder(ctx, APPROVALS)
            .setSmallIcon(R.drawable.ic_orb)
            .setContentTitle("Approve? ($risk)")
            .setContentText(summary)
            .setStyle(Notification.BigTextStyle().bigText(if (reason.isBlank()) summary else "$summary\n\n$reason"))
            .setCategory(Notification.CATEGORY_REMINDER)
            .setVisibility(Notification.VISIBILITY_PRIVATE)
            .setPublicVersion(hidden)
            .setContentIntent(openApp(ctx))
            .setAutoCancel(true)
            .addAction(action(true, R.string.approve))
            .addAction(action(false, R.string.deny))
            .build()
        post(ctx, idOf(id), n)
    }

    fun alert(ctx: Context, key: String, title: String, body: String) {
        val n = Notification.Builder(ctx, ALERTS)
            .setSmallIcon(R.drawable.ic_orb)
            .setContentTitle(title)
            .setContentText(body)
            .setStyle(Notification.BigTextStyle().bigText(body))
            .setVisibility(Notification.VISIBILITY_PRIVATE)
            .setPublicVersion(Notification.Builder(ctx, ALERTS).setSmallIcon(R.drawable.ic_orb).setContentTitle("JARVIS").build())
            .setContentIntent(openApp(ctx))
            .setAutoCancel(true)
            .build()
        post(ctx, idOf(key), n)
    }

    fun live(ctx: Context, text: String): Notification =
        Notification.Builder(ctx, LIVE)
            .setSmallIcon(R.drawable.ic_orb)
            .setContentTitle("Connected to JARVIS")
            .setContentText(text)
            .setOngoing(true)
            .setContentIntent(openApp(ctx))
            .build()

    fun cancel(ctx: Context, key: String) = ctx.getSystemService(NotificationManager::class.java).cancel(idOf(key))

    private fun post(ctx: Context, id: Int, n: Notification) {
        val nm = ctx.getSystemService(NotificationManager::class.java)
        if (nm.areNotificationsEnabled()) nm.notify(id, n)
    }
}
