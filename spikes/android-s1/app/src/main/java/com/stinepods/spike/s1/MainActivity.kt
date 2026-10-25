package com.stinepods.spike.s1

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import android.widget.ScrollView
import android.widget.TextView

/**
 * On-screen front end for [Engine]. Driven from adb, for example:
 *   adb shell am start -n com.stinepods.spike.s1/.MainActivity --es op take --es mac AA:BB:CC:DD:EE:FF
 * Prefer the background route (see [Listener]) when a foreground-only media app must keep playing.
 */
class MainActivity : Activity() {

    private lateinit var out: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        out = TextView(this).apply {
            setTextIsSelectable(true)
            textSize = 12f
            setPadding(24, 24, 24, 24)
        }
        setContentView(ScrollView(this).apply { addView(out) })
        if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.BLUETOOTH_CONNECT), 1)
        }
        Engine.screen = { msg -> runOnUiThread { out.append(msg + "\n") } }
        Engine.submit(this, intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        Engine.submit(this, intent)
    }

    override fun onDestroy() {
        Engine.screen = null
        super.onDestroy()
    }
}
