package com.tangenttech.hotsonos

import android.content.Context
import android.os.Handler
import android.os.Looper
import com.google.android.gms.wearable.MessageClient
import com.google.android.gms.wearable.Wearable
import org.json.JSONObject

/**
 * Wire strings match HotSonos.Remote.Protocol.WearWire. The protocol tests read this file.
 * The watch sends a command. The phone answers with JSON on the same path.
 */
object WearProtocol {
    const val PATH = "/hotsonos/v1"
    const val PLAY = "play"
    const val PAUSE = "pause"
    const val NEXT = "next"
    const val PREVIOUS = "previous"
    const val VOLUME_UP = "volume-up"
    const val VOLUME_DOWN = "volume-down"
    const val REFRESH = "refresh"
    const val UNKNOWN_VOLUME = 20
    const val VOLUME_STEP = 2
}

data class WearState(
    val title: String = "",
    val artist: String = "",
    val playing: Boolean = false,
    val volume: Int? = null,
)

class PhoneBridge(
    private val context: Context,
    private val onState: (WearState) -> Unit,
    private val onHint: (String) -> Unit,
) {
    private val main = Handler(Looper.getMainLooper())
    private var nodeId: String? = null
    private val listener = MessageClient.OnMessageReceivedListener { event ->
        if (event.path != WearProtocol.PATH) return@OnMessageReceivedListener
        val text = event.data?.toString(Charsets.UTF_8) ?: return@OnMessageReceivedListener
        parse(text)?.let { state -> main.post { onState(state) } }
    }

    fun start() {
        Wearable.getMessageClient(context).addListener(listener)
        refreshNodes()
    }

    fun stop() {
        Wearable.getMessageClient(context).removeListener(listener)
    }

    fun refreshNodes() {
        Wearable.getNodeClient(context).connectedNodes
            .addOnSuccessListener { nodes ->
                nodeId = nodes.firstOrNull { it.isNearby }?.id ?: nodes.firstOrNull()?.id
                if (nodeId == null) onHint("Pair this watch with the phone")
                else send(WearProtocol.REFRESH)
            }
            .addOnFailureListener { onHint("Open HotSonos on the phone") }
    }

    fun send(command: String) {
        val node = nodeId ?: return
        Wearable.getMessageClient(context)
            .sendMessage(node, WearProtocol.PATH, command.toByteArray(Charsets.UTF_8))
    }

    private fun parse(text: String): WearState? = try {
        val json = JSONObject(text)
        val title = json.optString("title")
        if (title.isEmpty()) null
        else WearState(
            title = title,
            artist = json.optString("artist"),
            playing = json.optBoolean("playing", false),
            volume = if (json.isNull("volume")) null else json.optInt("volume"),
        )
    } catch (_: Exception) {
        null
    }
}
