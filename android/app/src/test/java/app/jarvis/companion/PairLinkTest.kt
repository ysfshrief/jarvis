package app.jarvis.companion

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class PairLinkTest {
    private val fp = (1..32).joinToString(":") { "%02X".format(it * 7 % 256) }

    // Exactly what the PC's Settings → Devices puts in the QR code.
    private fun link(u: String = "https://192.168.1.20:47322", c: String = "K7QM-3XWP", f: String = fp) =
        "jarvis://pair?u=${java.net.URLEncoder.encode(u, "UTF-8")}&c=$c&fp=${java.net.URLEncoder.encode(f, "UTF-8")}"

    @Test
    fun reads_the_pc_link() {
        val (u, c, f) = JarvisClient.parsePairLink(link())!!
        assertEquals("https://192.168.1.20:47322", u)
        assertEquals("K7QM-3XWP", c)
        assertEquals(fp, f)
    }

    @Test
    fun normalizes_fingerprints() {
        assertEquals(fp, JarvisClient.normalize(fp.lowercase().replace(":", " ")))
    }

    @Test
    fun refuses_anything_else() {
        assertNull(JarvisClient.parsePairLink("https://192.168.1.20:47322/companion#pair=K7QM-3XWP"))
        assertNull(JarvisClient.parsePairLink(link(u = "http://192.168.1.20:47322"))) // never plain http
        assertNull(JarvisClient.parsePairLink(link(f = "AB:CD"))) // incomplete fingerprint
        assertNull(JarvisClient.parsePairLink("jarvis://pair?u=https%3A%2F%2Fx&c=K7QM-3XWP"))
        assertNull(JarvisClient.parsePairLink("not a link"))
    }
}
