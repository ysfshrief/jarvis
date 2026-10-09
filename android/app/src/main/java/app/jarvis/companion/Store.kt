package app.jarvis.companion

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** What this phone knows about the PC it paired with. */
data class Pairing(val url: String, val fingerprint: String, val token: String, val deviceName: String)

/**
 * Local storage. The device token is encrypted with a key that lives in the Android Keystore
 * (never exported, excluded from backups), so a copy of the app's files is not enough to act as this phone.
 */
object Store {
    private const val PREFS = "jarvis"
    private const val KEY_ALIAS = "jarvis-device-token"

    fun load(ctx: Context): Pairing? {
        val p = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val url = p.getString("url", null) ?: return null
        val fp = p.getString("fingerprint", null) ?: return null
        val token = p.getString("token", null)?.let(::decrypt) ?: return null
        return Pairing(url, fp, token, p.getString("name", "Phone") ?: "Phone")
    }

    fun save(ctx: Context, pairing: Pairing) {
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putString("url", pairing.url)
            .putString("fingerprint", pairing.fingerprint)
            .putString("token", encrypt(pairing.token))
            .putString("name", pairing.deviceName)
            .remove("seen.approvals").remove("seen.notifications").remove("seen.init")
            .apply()
    }

    fun clear(ctx: Context) {
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().clear().apply()
    }

    fun stayConnected(ctx: Context): Boolean = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean("live", false)
    fun setStayConnected(ctx: Context, on: Boolean) {
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean("live", on).apply()
    }

    fun seen(ctx: Context, kind: String): Set<String> =
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getStringSet("seen.$kind", emptySet()) ?: emptySet()

    fun setSeen(ctx: Context, kind: String, ids: Set<String>) {
        // Keep the set small: only the most recent ids matter.
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putStringSet("seen.$kind", ids.take(300).toSet()).apply()
    }

    fun initialized(ctx: Context): Boolean = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean("seen.init", false)
    fun setInitialized(ctx: Context) {
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean("seen.init", true).apply()
    }

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(
            KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        )
        return gen.generateKey()
    }

    private fun encrypt(plain: String): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val out = cipher.iv + cipher.doFinal(plain.toByteArray(Charsets.UTF_8))
        return Base64.encodeToString(out, Base64.NO_WRAP)
    }

    private fun decrypt(stored: String): String? = try {
        val bytes = Base64.decode(stored, Base64.NO_WRAP)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, bytes, 0, 12))
        String(cipher.doFinal(bytes, 12, bytes.size - 12), Charsets.UTF_8)
    } catch (e: Exception) {
        null
    }
}
