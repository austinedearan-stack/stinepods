package com.stinepods.app

import android.Manifest
import android.bluetooth.BluetoothA2dp
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothHeadset
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import org.lsposed.hiddenapibypass.HiddenApiBypass

/**
 * Android-version differences in one place (Android 10 → 16+).
 *
 * Headset control uses hidden Bluetooth methods. On current Android (checked against AOSP main):
 * - `BluetoothA2dp.connect/disconnect` are hidden-but-allowed and only need BLUETOOTH_CONNECT.
 * - `BluetoothHeadset.disconnect` is a @SystemApi that only needs BLUETOOTH_CONNECT, so it is reachable once the
 *   hidden-API filter is lifted for android.bluetooth (HiddenApiBypass, Apache-2.0).
 * - `BluetoothHeadset.connect` also needs MODIFY_PHONE_STATE (system-only) on newer versions; music doesn't need it.
 *
 * What actually works is recorded at runtime and shown on the settings screen, so a phone that differs (e.g. an OEM
 * Bluetooth stack) shows a clear warning instead of failing silently.
 */
object Compat {
    enum class Capability(val label: String, val essential: Boolean) {
        MUSIC_CONNECT("Connect headphones (music)", true),
        MUSIC_RELEASE("Release headphones (music)", true),
        CALL_RELEASE("Release headphones (calls)", true),
        CALL_CONNECT("Connect headphones (calls)", false),
    }

    enum class State { UNKNOWN, OK, BLOCKED }

    private val states = mutableMapOf<Capability, State>()
    private val listeners = mutableSetOf<() -> Unit>()

    fun init() {
        // Lift the hidden-API filter for android.bluetooth only. Android 9+ has the filter; earlier versions don't.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            runCatching { HiddenApiBypass.addHiddenApiExemptions("Landroid/bluetooth/") }
                .onFailure { Diag.warn("compat: hidden-API exemption failed: $it") }
        }
        // Methods that can't even be found are blocked here regardless of permissions.
        check(Capability.MUSIC_CONNECT, BluetoothA2dp::class.java, "connect")
        check(Capability.MUSIC_RELEASE, BluetoothA2dp::class.java, "disconnect")
        check(Capability.CALL_RELEASE, BluetoothHeadset::class.java, "disconnect")
        check(Capability.CALL_CONNECT, BluetoothHeadset::class.java, "connect")
    }

    private fun check(c: Capability, cls: Class<*>, method: String) {
        val found = runCatching { cls.getMethod(method, BluetoothDevice::class.java) }.isSuccess
        if (!found) set(c, State.BLOCKED)
    }

    /** Called with the outcome of each real attempt. */
    fun record(c: Capability, worked: Boolean) = set(c, if (worked) State.OK else State.BLOCKED)

    fun state(c: Capability) = synchronized(states) { states[c] ?: State.UNKNOWN }

    /** True when an essential capability is known not to work on this phone. */
    val degraded get() = Capability.entries.any { it.essential && state(it) == State.BLOCKED }

    private fun set(c: Capability, s: State) {
        val changed = synchronized(states) { (states.put(c, s) != s) }
        if (changed) synchronized(listeners) { listeners.toList() }.forEach { it() }
    }

    fun listen(l: () -> Unit) { synchronized(listeners) { listeners += l } }
    fun unlisten(l: () -> Unit) { synchronized(listeners) { listeners -= l } }

    /** Bluetooth is a runtime permission from Android 12; before that the manifest permissions suffice. */
    fun hasBluetoothPermission(context: Context) =
        Build.VERSION.SDK_INT < Build.VERSION_CODES.S ||
            context.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED

    /** Android 13+ blocks notification-listener access for sideloaded apps until "Allow restricted settings". */
    val restrictedSettingsLikely get() = Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU

    /**
     * Starts the service, tolerating Android 12+'s ban on starting foreground services from the background (the
     * listener or tile may be woken while the app isn't allowed to). The next allowed start picks things up.
     */
    fun startService(context: Context, action: String? = null) {
        try {
            StinePodsService.start(context, action)
        } catch (e: IllegalStateException) {
            Diag.warn("compat: service start not allowed right now: $e")
        } catch (e: SecurityException) {
            Diag.warn("compat: service start refused: $e")
        }
    }

    fun appDetails(context: Context) =
        Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS, android.net.Uri.parse("package:${context.packageName}"))
}
