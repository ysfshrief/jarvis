package app.jarvis.companion

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import org.json.JSONObject
import kotlin.concurrent.thread

/** Approve/Refuse pressed on a notification (Android asks for the screen lock first on Android 12+). */
class ApprovalReceiver : BroadcastReceiver() {
    override fun onReceive(ctx: Context, intent: Intent) {
        val id = intent.getStringExtra("id") ?: return
        val approve = intent.getBooleanExtra("approve", false)
        val pending = goAsync()
        thread {
            try {
                val p = Store.load(ctx) ?: return@thread
                val r = JarvisClient(p.url, p.fingerprint, p.token).post("/companion/api/approvals/$id", JSONObject().put("approve", approve))
                Notify.cancel(ctx, id)
                when {
                    r.ok -> Notify.alert(ctx, "done:$id", "JARVIS", if (approve) "Approved." else "Refused.")
                    r.code == 404 -> Notify.alert(ctx, "done:$id", "JARVIS", "That request was already answered or expired.")
                    else -> Notify.alert(ctx, "done:$id", "JARVIS", r.error())
                }
            } catch (e: Exception) {
                Notify.alert(ctx, "done:$id", "JARVIS", "Couldn't reach JARVIS — nothing was approved.")
            } finally {
                pending.finish()
            }
        }
    }
}
