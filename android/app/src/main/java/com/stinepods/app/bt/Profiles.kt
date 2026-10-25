package com.stinepods.app.bt

import android.annotation.SuppressLint
import android.bluetooth.BluetoothA2dp
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothClass
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothHeadset
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothProfile
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.Handler
import com.stinepods.app.Compat
import com.stinepods.app.Diag

/**
 * The headset's A2DP + HFP profiles (docs/spikes/S1-android-connect.md). Connect/disconnect use hidden profile
 * methods by reflection (see [Compat] for what each Android version allows). A headset counts as *here* while A2DP is connected
 * and as *away* once its whole Bluetooth link is down, because only then can the PC connect to a single-point headset.
 * All callbacks arrive on [handler]'s thread.
 */
@SuppressLint("MissingPermission")
class Profiles(
    private val context: Context,
    private val handler: Handler,
    private val onLink: (address: String, here: Boolean) -> Unit,
) {
    val adapter: BluetoothAdapter? = context.getSystemService(BluetoothManager::class.java).adapter
    private var a2dp: BluetoothA2dp? = null
    private var hfp: BluetoothHeadset? = null
    private val a2dpUp = mutableMapOf<String, Boolean>()
    private val aclUp = mutableMapOf<String, Boolean>()
    private val reported = mutableMapOf<String, Boolean>()
    private var watched = emptySet<String>()
    var onReady: (() -> Unit)? = null

    val ready get() = a2dp != null && hfp != null

    fun start() {
        val a = adapter ?: return Diag.warn("no Bluetooth adapter")
        context.registerReceiver(receiver, IntentFilter().apply {
            addAction(BluetoothA2dp.ACTION_CONNECTION_STATE_CHANGED)
            addAction(BluetoothDevice.ACTION_ACL_CONNECTED)
            addAction(BluetoothDevice.ACTION_ACL_DISCONNECTED)
            addAction(BluetoothAdapter.ACTION_STATE_CHANGED)
        }, null, handler)
        a.getProfileProxy(context, listener, BluetoothProfile.A2DP)
        a.getProfileProxy(context, listener, BluetoothProfile.HEADSET)
    }

    fun stop() {
        runCatching { context.unregisterReceiver(receiver) }
        a2dp?.let { adapter?.closeProfileProxy(BluetoothProfile.A2DP, it) }
        hfp?.let { adapter?.closeProfileProxy(BluetoothProfile.HEADSET, it) }
    }

    fun watch(headsets: Collection<String>) {
        watched = headsets.toSet()
        refresh()
    }

    fun isHere(h: String) = reported[h] == true

    /**
     * Connects A2DP (audio usable) and HFP; completion arrives as an onLink(here = true) callback.
     * Returns false when the request was refused outright.
     */
    fun take(h: String): Boolean {
        val d = device(h)
        val a2 = call(a2dp, "connect", d, Compat.Capability.MUSIC_CONNECT)
        // Calls are optional: on newer Android this needs a system-only permission, and most headsets
        // bring up the call profile themselves once music is connected.
        val hf = call(hfp, "connect", d, Compat.Capability.CALL_CONNECT)
        Diag.info("bt: take $h a2dp=$a2 hfp=$hf")
        return a2 == true
    }

    /** Drops both profiles so the Bluetooth link goes down; completion arrives as onLink(here = false). */
    fun release(h: String) {
        val d = device(h)
        val hf = call(hfp, "disconnect", d, Compat.Capability.CALL_RELEASE)
        val a2 = call(a2dp, "disconnect", d, Compat.Capability.MUSIC_RELEASE)
        Diag.info("bt: release $h hfp=$hf a2dp=$a2")
    }

    /** Paired devices for the settings screen: (address, name, looks like audio). */
    fun bonded(): List<Triple<String, String, Boolean>> = adapter?.bondedDevices.orEmpty().map {
        val audio = it.bluetoothClass?.majorDeviceClass == BluetoothClass.Device.Major.AUDIO_VIDEO
        Triple(it.address, it.name ?: it.address, audio)
    }.sortedBy { it.second.lowercase() }

    fun device(address: String): BluetoothDevice = adapter!!.getRemoteDevice(address)

    private fun call(proxy: BluetoothProfile?, name: String, d: BluetoothDevice, capability: Compat.Capability): Any? {
        val p = proxy ?: return false.also { Diag.warn("bt: $name: profile not ready") }
        return try {
            val result = p.javaClass.getMethod(name, BluetoothDevice::class.java).invoke(p, d)
            Compat.record(capability, true)
            result
        } catch (e: Exception) {
            // InvocationTargetException wraps the real failure (e.g. SecurityException on a stricter Android/OEM).
            val cause = e.cause ?: e
            Diag.warn("bt: $name failed: $cause")
            if (cause is SecurityException || e is NoSuchMethodException || e is IllegalAccessException) Compat.record(capability, false)
            false
        }
    }

    /** Re-reads current states (start-up, Bluetooth toggled, watch list changed). */
    private fun refresh() {
        val proxy = a2dp ?: return
        for (h in watched) {
            val up = proxy.getConnectionState(device(h)) == BluetoothProfile.STATE_CONNECTED
            a2dpUp[h] = up
            if (up) aclUp[h] = true
            report(h)
        }
    }

    private fun report(h: String) {
        if (h !in watched) return
        val a2 = a2dpUp[h] == true
        val acl = aclUp[h] == true
        val here = when {
            a2 -> true
            !acl -> false
            else -> reported[h] ?: false   // A2DP gone but link still up: wait for the link to drop
        }
        if (reported[h] == here) return
        reported[h] = here
        onLink(h, here)
    }

    private val listener = object : BluetoothProfile.ServiceListener {
        override fun onServiceConnected(profile: Int, proxy: BluetoothProfile) {
            handler.post {
                when (profile) {
                    BluetoothProfile.A2DP -> a2dp = proxy as BluetoothA2dp
                    BluetoothProfile.HEADSET -> hfp = proxy as BluetoothHeadset
                }
                refresh()
                if (ready) onReady?.invoke()
            }
        }

        override fun onServiceDisconnected(profile: Int) {
            handler.post {
                when (profile) {
                    BluetoothProfile.A2DP -> a2dp = null
                    BluetoothProfile.HEADSET -> hfp = null
                }
            }
        }
    }

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(c: Context, i: Intent) {
            if (i.action == BluetoothAdapter.ACTION_STATE_CHANGED) {
                if (i.getIntExtra(BluetoothAdapter.EXTRA_STATE, 0) == BluetoothAdapter.STATE_OFF) {
                    a2dpUp.clear(); aclUp.clear()
                    watched.forEach(::report)
                }
                return
            }
            @Suppress("DEPRECATION")
            val h = i.getParcelableExtra<BluetoothDevice>(BluetoothDevice.EXTRA_DEVICE)?.address ?: return
            when (i.action) {
                BluetoothA2dp.ACTION_CONNECTION_STATE_CHANGED ->
                    a2dpUp[h] = i.getIntExtra(BluetoothProfile.EXTRA_STATE, -1) == BluetoothProfile.STATE_CONNECTED
                BluetoothDevice.ACTION_ACL_CONNECTED -> aclUp[h] = true
                BluetoothDevice.ACTION_ACL_DISCONNECTED -> { aclUp[h] = false; a2dpUp[h] = false }
            }
            report(h)
        }
    }
}
