package com.stinepods.app

import android.service.notification.NotificationListenerService

/**
 * Exists only so the user can grant StinePods access to other apps' media sessions (needed to see what is playing and
 * to pause/resume it). It reads no notifications (docs/SECURITY.md #4).
 */
class MediaAccess : NotificationListenerService() {
    override fun onListenerConnected() {
        // Access was just granted (or the system rebound us): let the service start watching media.
        Compat.startService(this, StinePodsService.ACTION_RELOAD)
    }
}
