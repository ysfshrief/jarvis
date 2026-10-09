package app.jarvis.companion

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.net.http.SslError
import android.os.Build
import android.os.Bundle
import android.view.Menu
import android.view.MenuItem
import android.webkit.SslErrorHandler
import android.webkit.WebResourceRequest
import android.webkit.WebStorage
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.Button
import android.widget.EditText
import android.widget.TextView
import com.google.zxing.integration.android.IntentIntegrator
import org.json.JSONObject
import java.io.IOException
import kotlin.concurrent.thread

class MainActivity : Activity() {
    private var web: WebView? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Notify.channels(this)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        route(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        route(intent)
    }

    override fun onResume() {
        super.onResume()
        val p = Store.load(this) ?: return
        if (Store.stayConnected(this)) LiveService.start(this)
        thread { Poller.poll(applicationContext) }
        if (web == null && JarvisClient.parsePairLink(intent?.dataString ?: "") == null) showWeb(p)
    }

    private fun route(intent: Intent?) {
        val link = JarvisClient.parsePairLink(intent?.dataString ?: "")
        val paired = Store.load(this)
        when {
            link != null -> showPair(link)
            paired != null -> showWeb(paired)
            else -> showPair(null)
        }
    }

    // ---- Pairing ----

    private fun showPair(link: Triple<String, String, String>?) {
        web?.destroy()
        web = null
        setContentView(R.layout.activity_pair)
        val address = findViewById<EditText>(R.id.address)
        val code = findViewById<EditText>(R.id.code)
        val fingerprint = findViewById<EditText>(R.id.fingerprint)
        val name = findViewById<EditText>(R.id.name)
        val status = findViewById<TextView>(R.id.status)
        name.setText(Build.MODEL)
        link?.let { (u, c, fp) -> address.setText(u); code.setText(c); fingerprint.setText(fp) }

        findViewById<Button>(R.id.scan).setOnClickListener {
            IntentIntegrator(this)
                .setDesiredBarcodeFormats(IntentIntegrator.QR_CODE)
                .setPrompt("Scan the code in JARVIS → Settings → Devices")
                .setBeepEnabled(false)
                .setOrientationLocked(false)
                .initiateScan()
        }
        findViewById<Button>(R.id.pair).setOnClickListener { button ->
            val url = address.text.toString().trim().trimEnd('/')
            val fp = JarvisClient.normalize(fingerprint.text.toString())
            val pairCode = code.text.toString().trim().uppercase()
            val device = name.text.toString().trim().ifBlank { "Phone" }
            if (!url.startsWith("https://") || fp.length != 95 || pairCode.length < 8) {
                status.text = "Fill in the address (https://…), the code and the full fingerprint shown on the PC."
                return@setOnClickListener
            }
            button.isEnabled = false
            status.text = "Pairing…"
            thread {
                val message = try {
                    val r = JarvisClient(url, fp, null).post("/companion/api/pair", JSONObject().put("code", pairCode).put("deviceName", device))
                    if (r.ok) {
                        Store.save(this, Pairing(url, fp, r.json().getString("token"), device))
                        Poller.scheduleBackground(this)
                        null
                    } else r.error()
                } catch (e: WrongCertificateException) {
                    "${e.message} Check the address and the fingerprint on the PC."
                } catch (e: IOException) {
                    "Can't reach JARVIS at $url. Is this phone on the same Wi-Fi, and is the phone companion on?"
                } catch (e: Exception) {
                    "Pairing failed: ${e.message}"
                }
                runOnUiThread {
                    button.isEnabled = true
                    if (message == null) {
                        setIntent(Intent(this, MainActivity::class.java))
                        Store.load(this)?.let(::showWeb)
                    } else status.text = message
                }
            }
        }
    }

    @Deprecated("Activity result API is not used to keep this app dependency-free.")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        val result = IntentIntegrator.parseActivityResult(requestCode, resultCode, data)
        if (result == null) {
            @Suppress("DEPRECATION")
            super.onActivityResult(requestCode, resultCode, data)
            return
        }
        val contents = result.contents ?: return
        val link = JarvisClient.parsePairLink(contents)
        if (link != null) showPair(link)
        else findViewById<TextView>(R.id.status)?.text = "That QR code isn't a JARVIS pairing code."
    }

    // ---- Companion page ----

    @SuppressLint("SetJavaScriptEnabled")
    private fun showWeb(p: Pairing) {
        if (web != null) return
        val view = WebView(this)
        view.settings.javaScriptEnabled = true
        view.settings.domStorageEnabled = true
        view.settings.allowFileAccess = false
        view.settings.allowContentAccess = false
        view.webViewClient = object : WebViewClient() {
            // JARVIS's certificate is self-made, so the WebView always asks; only the pinned one is accepted.
            @SuppressLint("WebViewClientOnReceivedSslError")
            override fun onReceivedSslError(v: WebView, handler: SslErrorHandler, error: SslError) {
                val cert = error.certificate.x509Certificate
                if (cert != null && JarvisClient.fingerprintOf(cert) == p.fingerprint && error.url.startsWith(p.url)) handler.proceed()
                else {
                    handler.cancel()
                    runOnUiThread { AlertDialog.Builder(this@MainActivity).setMessage("This isn't the JARVIS you paired with. Nothing was sent.").setPositiveButton("OK", null).show() }
                }
            }

            // Stay on JARVIS; other links open in the phone's browser.
            override fun shouldOverrideUrlLoading(v: WebView, request: WebResourceRequest): Boolean {
                val url = request.url.toString()
                if (url.startsWith(p.url)) return false
                if (request.url.scheme == "https") startActivity(Intent(Intent.ACTION_VIEW, request.url))
                return true
            }
        }
        setContentView(view)
        web = view
        view.loadUrl("${p.url}/companion#token=${Uri.encode(p.token)}")
    }

    @Deprecated("Back handling for plain Activity")
    override fun onBackPressed() {
        val w = web
        if (w != null && w.canGoBack()) {
            w.goBack()
            return
        }
        @Suppress("DEPRECATION")
        super.onBackPressed()
    }

    // ---- Menu ----

    override fun onCreateOptionsMenu(menu: Menu): Boolean {
        menuInflater.inflate(R.menu.main, menu)
        return true
    }

    override fun onPrepareOptionsMenu(menu: Menu): Boolean {
        val paired = Store.load(this) != null
        menu.findItem(R.id.stay_connected).isVisible = paired
        menu.findItem(R.id.stay_connected).isChecked = Store.stayConnected(this)
        menu.findItem(R.id.check_now).isVisible = paired
        menu.findItem(R.id.unpair).isVisible = paired
        return true
    }

    override fun onOptionsItemSelected(item: MenuItem): Boolean {
        when (item.itemId) {
            R.id.stay_connected -> {
                val on = !Store.stayConnected(this)
                if (on) {
                    AlertDialog.Builder(this)
                        .setTitle(R.string.stay_connected)
                        .setMessage("JARVIS will check your PC every 15 seconds so approvals reach you before they expire. Android shows a permanent notification while this is on, and it uses some battery. Without it, alerts arrive about every 15 minutes and approvals only while the app is open.")
                        .setPositiveButton("Turn on") { _, _ -> Store.setStayConnected(this, true); LiveService.start(this); invalidateOptionsMenu() }
                        .setNegativeButton("Cancel", null)
                        .show()
                } else {
                    Store.setStayConnected(this, false)
                    LiveService.stop(this)
                    invalidateOptionsMenu()
                }
            }
            R.id.check_now -> thread {
                val outcome = Poller.poll(applicationContext)
                runOnUiThread {
                    val text = when (outcome) {
                        PollOutcome.Ok -> "Up to date."
                        PollOutcome.Unreachable -> "Can't reach your PC right now."
                        PollOutcome.WrongCertificate -> "Refused: that server isn't your JARVIS."
                        PollOutcome.NotPaired, PollOutcome.Unpaired -> "This phone isn't paired."
                    }
                    android.widget.Toast.makeText(this, text, android.widget.Toast.LENGTH_SHORT).show()
                    if (outcome == PollOutcome.Unpaired) showPair(null)
                }
            }
            R.id.unpair -> AlertDialog.Builder(this)
                .setMessage("Forget JARVIS on this phone? Also remove the phone on the PC (Settings → Devices) to revoke its access there.")
                .setPositiveButton("Unpair") { _, _ ->
                    Poller.stopBackground(this)
                    Store.clear(this)
                    WebStorage.getInstance().deleteAllData()
                    showPair(null)
                    invalidateOptionsMenu()
                }
                .setNegativeButton("Cancel", null)
                .show()
            else -> return super.onOptionsItemSelected(item)
        }
        return true
    }

    override fun onDestroy() {
        web?.destroy()
        super.onDestroy()
    }
}
