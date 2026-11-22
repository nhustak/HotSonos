package com.tangenttech.hotsonos

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.wear.compose.material.MaterialTheme

class MainActivity : ComponentActivity() {
    private lateinit var bridge: PhoneBridge

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            var state by remember { mutableStateOf(WearState()) }
            var hint by remember { mutableStateOf("Open HotSonos on the phone") }
            var ready by remember { mutableStateOf(false) }
            val created = remember {
                PhoneBridge(
                    context = this@MainActivity,
                    onState = { next ->
                        state = next
                        ready = true
                    },
                    onHint = { hint = it },
                ).also { bridge = it }
            }
            DisposableEffect(created) {
                created.start()
                onDispose { created.stop() }
            }
            MaterialTheme {
                PlayerScreen(
                    state = state,
                    hint = hint,
                    ready = ready,
                    onPrevious = { created.send(WearProtocol.PREVIOUS) },
                    onPlayPause = {
                        created.send(if (state.playing) WearProtocol.PAUSE else WearProtocol.PLAY)
                        state = state.copy(playing = !state.playing)
                    },
                    onNext = { created.send(WearProtocol.NEXT) },
                    onVolume = { direction ->
                        val current = state.volume ?: WearProtocol.UNKNOWN_VOLUME
                        val next = (current + direction * WearProtocol.VOLUME_STEP).coerceIn(0, 100)
                        state = state.copy(volume = next)
                        created.send(if (direction > 0) WearProtocol.VOLUME_UP else WearProtocol.VOLUME_DOWN)
                    },
                )
            }
        }
    }

    override fun onResume() {
        super.onResume()
        if (::bridge.isInitialized) bridge.refreshNodes()
    }
}
