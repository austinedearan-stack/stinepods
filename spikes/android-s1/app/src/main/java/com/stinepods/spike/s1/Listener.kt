package com.stinepods.spike.s1

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.service.notification.NotificationListenerService

/**
 * Grants access to other apps' media sessions (reads no notifications), and, because the system keeps it bound,
 * doubles as a background entry point so adb can drive [Engine] without bringing an activity to the front:
 *   adb shell am broadcast -a com.stinepods.spike.s1.CMD --es op play --es pkg youtube
 */
class Listener : NotificationListenerService() {

    private val commands = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) = Engine.submit(context, intent)
    }

    override fun onListenerConnected() {
        // Exported so adb's shell can reach it; spike-only, the real app will not accept outside commands.
        registerReceiver(commands, IntentFilter(Engine.CMD_ACTION), RECEIVER_EXPORTED)
    }

    override fun onListenerDisconnected() {
        unregisterReceiver(commands)
    }
}
