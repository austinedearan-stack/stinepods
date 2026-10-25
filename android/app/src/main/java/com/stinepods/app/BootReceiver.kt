package com.stinepods.app

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** Starts StinePods after a reboot or an app update, once it has been set up. */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED && intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        if (Prefs(context).pc != null) StinePodsService.start(context)
    }
}
