package com.stinepods.app

import android.service.quicksettings.Tile
import android.service.quicksettings.TileService

/** Quick Settings tile: turn automatic switching on or off. */
class SwitchTile : TileService() {
    override fun onStartListening() = render()

    override fun onClick() {
        val prefs = Prefs(this)
        prefs.enabled = !prefs.enabled
        Compat.startService(this, StinePodsService.ACTION_RELOAD)
        render()
    }

    private fun render() {
        val tile = qsTile ?: return
        tile.state = if (Prefs(this).enabled) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
        tile.subtitle = if (Prefs(this).enabled) "Auto switch" else "Paused"
        tile.updateTile()
    }
}
