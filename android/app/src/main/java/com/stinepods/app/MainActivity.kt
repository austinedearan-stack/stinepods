package com.stinepods.app

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.app.NotificationManager
import android.bluetooth.BluetoothClass
import android.bluetooth.BluetoothManager
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.AdapterView
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.LinearLayout
import android.widget.RadioButton
import android.widget.RadioGroup
import android.widget.ScrollView
import android.widget.Spinner
import android.widget.Switch
import android.widget.TextView
import com.stinepods.app.media.MediaWatch
import com.stinepods.core.AppRule
import com.stinepods.core.Prefer
import com.stinepods.core.ReturnOnStop

/** Setup and settings. Plain views built in code (no UI libraries, so the app stays small), styled as cards. */
@SuppressLint("MissingPermission", "SetTextI18n", "UseSwitchCompatOrMaterialCode")
class MainActivity : Activity() {
    private lateinit var prefs: Prefs
    private lateinit var page: LinearLayout
    private lateinit var target: LinearLayout
    private var status: TextView? = null
    private var dot: View? = null
    private val statusListener: (String) -> Unit = { s -> runOnUiThread { showStatus(s) } }

    // Always-dark look in the style of headphone companion apps: black page, dark cards, warm orange accent.
    private val accent = Color.rgb(255, 138, 0)
    private val bg = Color.BLACK
    private val surface = Color.rgb(28, 28, 30)
    private val textMain = Color.rgb(240, 240, 242)
    private val textDim = Color.rgb(142, 142, 147)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        prefs = Prefs(this)
        page = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(16), dp(20), dp(16), dp(32)) }
        val scroll = ScrollView(this).apply { setBackgroundColor(bg); addView(page) }
        // Android 15+ draws apps edge to edge: keep content clear of the status and navigation bars.
        scroll.setOnApplyWindowInsetsListener { v, insets -> v.setPadding(0, barTop(insets), 0, barBottom(insets)); insets }
        setContentView(scroll)
        @Suppress("DEPRECATION")
        window.statusBarColor = bg
        actionBar?.hide()
    }

    @Suppress("DEPRECATION")
    private fun barTop(i: android.view.WindowInsets) =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) i.getInsets(android.view.WindowInsets.Type.systemBars()).top else i.systemWindowInsetTop

    @Suppress("DEPRECATION")
    private fun barBottom(i: android.view.WindowInsets) =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) i.getInsets(android.view.WindowInsets.Type.systemBars()).bottom else i.systemWindowInsetBottom

    private val compatListener: () -> Unit ={ runOnUiThread { if (!isFinishing) build() } }

    override fun onResume() {
        super.onResume()
        build()
        Diag.listen(statusListener)
        Compat.listen(compatListener)
        if (hasBluetooth()) StinePodsService.start(this, StinePodsService.ACTION_RELOAD)
    }

    override fun onPause() {
        Diag.unlisten(statusListener)
        Compat.unlisten(compatListener)
        super.onPause()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) = build()

    private fun build() {
        page.removeAllViews()
        target = page
        add(TextView(this).apply { text = "StinePods"; textSize = 15f; setTypeface(typeface, Typeface.BOLD); setTextColor(textDim); letterSpacing = 0.08f }, 0)
        hero()
        showStatus(Diag.status)

        setupCard()
        if (!hasBluetooth()) return
        compatCard()

        val bonded = getSystemService(BluetoothManager::class.java).adapter?.bondedDevices.orEmpty()
            .map { Triple(it.address, it.name ?: it.address, it.bluetoothClass?.majorDeviceClass == BluetoothClass.Device.Major.AUDIO_VIDEO) }
            .sortedBy { it.second.lowercase() }
        card("Devices") {
            picker("PC running StinePods", bonded.filter { !it.third }, prefs.pc) { prefs.pc = it; changed() }
            picker("Headphones", bonded.filter { it.third }, prefs.headset) { prefs.headset = it; changed() }
            hint("Pair the phone and the PC in Bluetooth settings first. The first time they connect, both show a 6-digit code to confirm.")
        }

        card("Behaviour") {
            choice("When nothing is playing, the headphones prefer", Prefer.entries, prefs.prefer,
                mapOf(Prefer.PHONE to "This phone", Prefer.PC to "The PC", Prefer.NONE to "No preference")) { prefs.prefer = it; changed() }
            choice("When playback stops", ReturnOnStop.entries, prefs.returnOnStop,
                mapOf(ReturnOnStop.STAY to "Keep the headphones here", ReturnOnStop.RETURN to "Give them back to the other device")) { prefs.returnOnStop = it; changed() }
            toggle("Phone calls take the headphones", prefs.callsTakeHeadset) { prefs.callsTakeHeadset = it; changed() }
        }

        card("Apps") {
            hint("What happens when an app starts playing while the headphones are on the PC.")
            val rules = prefs.rules()
            for (app in listOf(MediaWatch.OTHER) + prefs.seenApps.sortedBy { label(it).lowercase() }) {
                val rule = rules[app] ?: AppRule.AUTO
                picker(label(app), AppRule.entries.map { Triple(it.name, ruleLabel(it), true) }, rule.name, allowNone = false) { r ->
                    prefs.apps = prefs.apps + (app to AppRule.valueOf(r!!)); changed()
                }
            }
            if (prefs.seenApps.isEmpty()) hint("Apps you play media in appear here.")
        }
    }

    /** Big headphones card: device name, live status, and the main on/off switch. */
    private fun hero() {
        val c = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(dp(20), dp(28), dp(20), dp(20))
            background = GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM, intArrayOf(Color.rgb(44, 44, 48), surface)).apply { cornerRadius = dp(24).toFloat() }
        }
        page.addView(c, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(12) })
        c.addView(android.widget.ImageView(this).apply {
            setImageResource(R.drawable.ic_stat)
            imageTintList = ColorStateList.valueOf(textMain)
        }, LinearLayout.LayoutParams(dp(96), dp(96)))
        val name = prefs.headset?.let { h ->
            runCatching { getSystemService(BluetoothManager::class.java).adapter?.getRemoteDevice(h)?.name }.getOrNull()
        } ?: "No headphones chosen"
        c.addView(TextView(this).apply { text = name; textSize = 24f; setTypeface(typeface, Typeface.BOLD); setTextColor(textMain); gravity = Gravity.CENTER },
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(14) })
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER }
        dot = View(this).apply { background = circle(textDim) }
        row.addView(dot, LinearLayout.LayoutParams(dp(8), dp(8)).apply { marginEnd = dp(8) })
        status = TextView(this).apply { textSize = 14f; setTextColor(textDim) }
        row.addView(status)
        c.addView(row, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(6) })
        target = c
        toggle("Automatic switching", prefs.enabled) { prefs.enabled = it; changed() }
        target = page
    }

    private fun setupCard() {
        val nm = getSystemService(NotificationManager::class.java)
        val items = buildList {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) add(Triple("Bluetooth access", hasBluetooth()) {
                requestPermissions(arrayOf(Manifest.permission.BLUETOOTH_CONNECT), 1)
            })
            if (Build.VERSION.SDK_INT >= 33) add(Triple("Notifications for prompts and pairing",
                checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) {
                requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 2)
            })
            add(Triple("Media access: see and pause what's playing (no notifications are read)",
                nm.isNotificationListenerAccessGranted(ComponentName(this@MainActivity, MediaAccess::class.java))) {
                startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
            })
            add(Triple("Keep running in the background", getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(packageName)) {
                startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName")))
            })
        }
        if (items.all { it.second }) {
            card { add(TextView(this).apply { text = "✓  All set up"; textSize = 14f; setTextColor(accent) }, 0) }
            return
        }
        val mediaOk = nm.isNotificationListenerAccessGranted(ComponentName(this, MediaAccess::class.java))
        card("Finish setting up") {
            for ((label, ok, fix) in items) statusRow(if (ok) "✓" else "!", if (ok) accent else warn, label, if (ok) null else ("Fix" to fix))
            if (!mediaOk && Compat.restrictedSettingsLikely) {
                hint("If Android says Media access is a \"restricted setting\": open App info, tap ⋮ (top right) → Allow restricted settings, then come back and tap Fix again.")
                add(button("Open App info") { startActivity(Compat.appDetails(this)) }, 8)
            }
        }
    }

    /** What this phone's Bluetooth lets StinePods do, from real attempts (see Compat). */
    private fun compatCard() {
        card("Phone compatibility") {
            hint("Android ${Build.VERSION.RELEASE} · ${Build.MANUFACTURER} ${Build.MODEL}")
            for (c in Compat.Capability.entries) {
                val s = Compat.state(c)
                val (mark, color, note) = when (s) {
                    Compat.State.OK -> Triple("✓", accent, "")
                    Compat.State.UNKNOWN -> Triple("•", textDim, " (checked on first switch)")
                    Compat.State.BLOCKED -> Triple("✕", if (c.essential) warn else textDim, if (c.essential) " — blocked on this phone" else " — not available (optional)")
                }
                statusRow(mark, color, c.label + note, null)
            }
            if (Compat.degraded) hint("This phone's Bluetooth refuses part of the automatic switching. Switching from the PC still works; to move the headphones to the PC, disconnect them from this phone's Bluetooth settings.")
        }
    }

    private val warn = Color.rgb(230, 126, 34)

    private fun statusRow(mark: String, color: Int, label: String, action: Pair<String, () -> Unit>?) {
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        row.addView(TextView(this).apply {
            text = mark; gravity = Gravity.CENTER; textSize = 13f; setTextColor(Color.WHITE); background = circle(color)
        }, LinearLayout.LayoutParams(dp(24), dp(24)).apply { marginEnd = dp(12) })
        row.addView(TextView(this).apply { text = label; textSize = 14f; setTextColor(textMain) },
            LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        if (action != null) row.addView(button(action.first, action.second))
        add(row, 10)
    }

    private fun showStatus(s: String) {
        status?.text = s
        val good = s.contains("PC: ") && !s.startsWith("Paused")
        dot?.background = circle(if (good) accent else if (s.startsWith("Paused")) textDim else Color.rgb(230, 126, 34))
    }

    private fun changed() = StinePodsService.start(this, StinePodsService.ACTION_RELOAD)

    private fun hasBluetooth() = Compat.hasBluetoothPermission(this)

    private fun ruleLabel(r: AppRule) = when (r) {
        AppRule.AUTO -> "Switch automatically"
        AppRule.ASK -> "Ask me first"
        AppRule.IGNORE -> "Never switch"
    }

    private fun label(app: String) = if (app == MediaWatch.OTHER) "Other apps (Instagram, gallery videos, games…)" else runCatching {
        packageManager.getApplicationLabel(packageManager.getApplicationInfo(app, 0)).toString()
    }.getOrDefault(app)

    // ---- small view helpers ----

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

    private fun circle(color: Int) = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(color) }

    private fun add(v: View, top: Int = 8): View {
        target.addView(v, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(top) })
        return v
    }

    /** A rounded surface card; views added inside [body] go into it. */
    private fun card(title: String? = null, body: () -> Unit) {
        val c = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(14), dp(16), dp(16))
            background = GradientDrawable().apply { cornerRadius = dp(16).toFloat(); setColor(surface) }
            elevation = dp(1).toFloat()
        }
        page.addView(c, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(14) })
        target = c
        if (title != null) add(TextView(this).apply { text = title; textSize = 17f; setTypeface(typeface, Typeface.BOLD); setTextColor(textMain) }, 0)
        body()
        target = page
    }

    private fun hint(s: String) = add(TextView(this).apply { text = s; textSize = 12f; setTextColor(textDim) }, 10)

    private fun button(label: String, onClick: () -> Unit) = Button(this).apply {
        text = label
        isAllCaps = false
        setTextColor(Color.WHITE)
        background = GradientDrawable().apply { cornerRadius = dp(20).toFloat(); setColor(accent) }
        minHeight = 0; minimumHeight = 0
        setPadding(dp(16), dp(6), dp(16), dp(6))
        setOnClickListener { onClick() }
    }

    private fun toggle(label: String, on: Boolean, set: (Boolean) -> Unit) = add(Switch(this).apply {
        text = label; textSize = 15f; setTextColor(textMain); isChecked = on
        thumbTintList = ColorStateList(arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()), intArrayOf(accent, textDim))
        setOnCheckedChangeListener { _, v -> set(v) }
    }, 12)

    private fun caption(s: String, top: Int = 14) = add(TextView(this).apply { text = s; textSize = 14f; setTextColor(textDim) }, top)

    private fun <E : Enum<E>> choice(title: String, values: List<E>, current: E, names: Map<E, String>, set: (E) -> Unit) {
        caption(title)
        val group = RadioGroup(this)
        for (v in values) group.addView(RadioButton(this).apply {
            id = View.generateViewId(); text = names[v]; textSize = 15f; setTextColor(textMain); isChecked = v == current
            buttonTintList = ColorStateList.valueOf(accent)
            setOnClickListener { set(v) }
        })
        add(group, 2)
    }

    /** Spinner over (value, label, _) items; calls [set] with the chosen value when the user changes it. */
    private fun picker(title: String, items: List<Triple<String, String, Boolean>>, current: String?, allowNone: Boolean = true, set: (String?) -> Unit) {
        caption(title)
        val options = (if (allowNone) listOf<Pair<String?, String>>(null to "Choose…") else emptyList()) + items.map { it.first to it.second }
        val spinner = Spinner(this)
        spinner.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item, options.map { it.second })
        spinner.setSelection(options.indexOfFirst { it.first == current }.coerceAtLeast(0), false)
        spinner.background = GradientDrawable().apply {
            cornerRadius = dp(10).toFloat(); setStroke(dp(1), Color.rgb(58, 58, 60))
        }
        spinner.setPadding(dp(8), dp(4), dp(8), dp(4))
        spinner.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onItemSelected(p: AdapterView<*>?, v: View?, pos: Int, id: Long) {
                val value = options[pos].first
                if (value != current) set(value)
            }
            override fun onNothingSelected(p: AdapterView<*>?) = Unit
        }
        add(spinner, 4)
    }
}
