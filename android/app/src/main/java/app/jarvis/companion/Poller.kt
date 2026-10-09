package app.jarvis.companion

import android.content.Context
import androidx.work.Constraints
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.NetworkType
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.Worker
import androidx.work.WorkerParameters
import org.json.JSONArray
import java.io.IOException
import java.util.concurrent.TimeUnit

enum class PollOutcome { Ok, NotPaired, Unpaired, Unreachable, WrongCertificate }

/** Asks JARVIS what's waiting and turns new items into Android notifications. */
object Poller {
    @Synchronized
    fun poll(ctx: Context): PollOutcome {
        val p = Store.load(ctx) ?: return PollOutcome.NotPaired
        val api = JarvisClient(p.url, p.fingerprint, p.token)
        try {
            val approvals = api.get("/companion/api/approvals")
            if (approvals.code == 401) {
                Notify.alert(ctx, "unpaired", "JARVIS", "This phone was removed on the PC. Pair again to use it.")
                Store.clear(ctx)
                stopBackground(ctx)
                return PollOutcome.Unpaired
            }
            val first = !Store.initialized(ctx)
            if (approvals.ok) {
                val list = JSONArray(approvals.body)
                val current = mutableSetOf<String>()
                val seen = Store.seen(ctx, "approvals")
                for (i in 0 until list.length()) {
                    val a = list.getJSONObject(i)
                    val id = a.getString("id")
                    current += id
                    if (id !in seen) Notify.approval(ctx, id, a.optString("summary"), a.optString("risk"), a.optString("reason"))
                }
                // Answered on the PC or expired: the buttons would do nothing now.
                (seen - current).forEach { Notify.cancel(ctx, it) }
                Store.setSeen(ctx, "approvals", current)
            }
            val alerts = api.get("/companion/api/notifications?limit=20")
            if (alerts.ok) {
                val list = JSONArray(alerts.body)
                val seen = Store.seen(ctx, "notifications")
                val ids = mutableSetOf<String>()
                for (i in 0 until list.length()) {
                    val n = list.getJSONObject(i)
                    val id = n.getString("id")
                    ids += id
                    // The first check after pairing only learns what's already there instead of replaying it.
                    if (!first && id !in seen) Notify.alert(ctx, "n:$id", n.optString("title", "JARVIS"), n.optString("body"))
                }
                Store.setSeen(ctx, "notifications", seen + ids)
            }
            Store.setInitialized(ctx)
            return PollOutcome.Ok
        } catch (e: WrongCertificateException) {
            return PollOutcome.WrongCertificate
        } catch (e: IOException) {
            return PollOutcome.Unreachable // not on the same network right now
        } catch (e: org.json.JSONException) {
            return PollOutcome.Unreachable
        }
    }

    /** Android runs this at most every 15 minutes — fine for alerts; approvals need "Stay connected". */
    fun scheduleBackground(ctx: Context) {
        val req = PeriodicWorkRequestBuilder<PollWorker>(15, TimeUnit.MINUTES)
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
            .build()
        WorkManager.getInstance(ctx).enqueueUniquePeriodicWork("jarvis-poll", ExistingPeriodicWorkPolicy.KEEP, req)
    }

    fun stopBackground(ctx: Context) {
        WorkManager.getInstance(ctx).cancelUniqueWork("jarvis-poll")
        Store.setStayConnected(ctx, false)
        LiveService.stop(ctx)
    }
}

class PollWorker(ctx: Context, params: WorkerParameters) : Worker(ctx, params) {
    override fun doWork(): Result {
        Poller.poll(applicationContext)
        return Result.success()
    }
}
