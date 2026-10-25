package com.stinepods.app

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import com.stinepods.core.KeyStore
import java.security.KeyStore as JavaKeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Long-term pairing keys (docs/DECISIONS.md D12), stored in app-private prefs, each sealed with AES-GCM under a
 * non-exportable Android Keystore key.
 */
class KeyVault(context: Context) : KeyStore {
    private val sp = context.getSharedPreferences("stinepods-keys", Context.MODE_PRIVATE)

    override fun get(peerId: String): ByteArray? {
        val sealed = sp.getString(peerId, null) ?: return null
        return try {
            val raw = Base64.decode(sealed, Base64.NO_WRAP)
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, wrappingKey(), GCMParameterSpec(128, raw, 0, 12))
            cipher.doFinal(raw, 12, raw.size - 12)
        } catch (e: Exception) {
            Diag.warn("pairing key for $peerId unreadable, will re-pair: $e")
            null
        }
    }

    override fun put(peerId: String, key: ByteArray) {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, wrappingKey())
        val sealed = cipher.iv + cipher.doFinal(key)
        sp.edit().putString(peerId, Base64.encodeToString(sealed, Base64.NO_WRAP)).apply()
    }

    private fun wrappingKey(): SecretKey {
        val ks = JavaKeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").run {
            init(
                KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(256)
                    .build(),
            )
            generateKey()
        }
    }

    private companion object {
        const val ALIAS = "stinepods-pairing"
    }
}
