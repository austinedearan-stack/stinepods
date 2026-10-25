package com.stinepods.app

import android.content.Context
import com.stinepods.app.media.MediaWatch
import com.stinepods.core.AppRule
import com.stinepods.core.Prefer
import com.stinepods.core.ReturnOnStop
import com.stinepods.core.Role
import com.stinepods.core.Settings
import java.util.UUID

/** User settings, in app-private SharedPreferences. The phone owns the shared config (protocol `config`). */
class Prefs(context: Context) {
    private val sp = context.getSharedPreferences("stinepods", Context.MODE_PRIVATE)

    val deviceId: String = sp.getString("id", null) ?: UUID.randomUUID().toString().also { sp.edit().putString("id", it).apply() }

    var enabled: Boolean
        get() = sp.getBoolean("enabled", true)
        set(v) = sp.edit().putBoolean("enabled", v).apply()

    /** Bluetooth address of the paired PC running the StinePods agent. */
    var pc: String?
        get() = sp.getString("pc", null)
        set(v) = sp.edit().putString("pc", v).apply()

    var headset: String?
        get() = sp.getString("headset", null)
        set(v) = sp.edit().putString("headset", v).apply()

    var prefer: Prefer
        get() = enumOr(sp.getString("prefer", null), Prefer.PHONE)
        set(v) = sp.edit().putString("prefer", v.name).apply()

    var returnOnStop: ReturnOnStop
        get() = enumOr(sp.getString("returnOnStop", null), ReturnOnStop.STAY)
        set(v) = sp.edit().putString("returnOnStop", v.name).apply()

    var callsTakeHeadset: Boolean
        get() = sp.getBoolean("callsTakeHeadset", false)
        set(v) = sp.edit().putBoolean("callsTakeHeadset", v).apply()

    /** Per-app Ask/Auto/Ignore (docs/DECISIONS.md: Ask vs Auto is set per app). */
    var apps: Map<String, AppRule>
        get() = sp.getStringSet("apps", emptySet())!!.mapNotNull { e ->
            val (pkg, rule) = e.split('=', limit = 2).takeIf { it.size == 2 } ?: return@mapNotNull null
            pkg to enumOr(rule, AppRule.AUTO)
        }.toMap()
        set(v) = sp.edit().putStringSet("apps", v.map { (k, r) -> "$k=${r.name}" }.toSet()).apply()

    /** Media apps seen on this phone, so the settings screen can offer a rule for each. */
    var seenApps: Set<String>
        get() = sp.getStringSet("seenApps", emptySet())!!
        set(v) = sp.edit().putStringSet("seenApps", v).apply()

    fun settings() = Settings(
        role = Role.PHONE,
        headsets = listOfNotNull(headset),
        prefer = prefer,
        returnOnStop = returnOnStop,
        callsTakeHeadset = callsTakeHeadset,
        apps = rules(),
    )

    /** Per-app rules with defaults applied. Full: apps without media controls ask first (owner's choice). Lite: all media is one "app", switching automatically. */
    fun rules(): Map<String, AppRule> = mapOf(MediaWatch.OTHER to if (BuildConfig.FULL) AppRule.ASK else AppRule.AUTO) + apps

    private inline fun <reified E : Enum<E>> enumOr(name: String?, default: E): E =
        enumValues<E>().firstOrNull { it.name == name } ?: default
}
