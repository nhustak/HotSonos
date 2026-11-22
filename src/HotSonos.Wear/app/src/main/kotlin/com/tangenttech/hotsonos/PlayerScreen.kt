package com.tangenttech.hotsonos

import androidx.compose.foundation.focusable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.input.rotary.onRotaryScrollEvent
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.wear.compose.material.CompactButton
import androidx.wear.compose.material.MaterialTheme
import androidx.wear.compose.material.Scaffold
import androidx.wear.compose.material.Text

@Composable
fun PlayerScreen(
    state: WearState,
    hint: String,
    ready: Boolean,
    onPrevious: () -> Unit,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    onVolume: (Int) -> Unit,
) {
    val focusRequester = remember { FocusRequester() }
    val carry = remember { floatArrayOf(0f) }
    LaunchedEffect(Unit) {
        try {
            focusRequester.requestFocus()
        } catch (_: IllegalStateException) {
        }
    }

    val headline = if (ready && state.title.isNotBlank()) state.title else hint
    val artist = if (ready) state.artist else ""
    Scaffold {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = 28.dp, vertical = 8.dp)
                .onRotaryScrollEvent { event ->
                    // Positive pixels scroll a list downward. Treat that as quieter.
                    carry[0] += event.verticalScrollPixels
                    val threshold = 48f
                    var down = 0
                    var up = 0
                    while (carry[0] >= threshold && down < 3) {
                        carry[0] -= threshold
                        down++
                    }
                    while (carry[0] <= -threshold && up < 3) {
                        carry[0] += threshold
                        up++
                    }
                    repeat(down) { onVolume(-1) }
                    repeat(up) { onVolume(1) }
                    true
                }
                .focusRequester(focusRequester)
                .focusable(),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center,
        ) {
            Text(
                text = headline,
                textAlign = TextAlign.Center,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.title3,
            )
            Text(
                text = artist,
                textAlign = TextAlign.Center,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.body2,
            )
            Row(
                modifier = Modifier.padding(top = 6.dp),
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                CompactButton(onClick = onPrevious) { Text("|<") }
                CompactButton(onClick = onPlayPause) { Text(if (state.playing) "||" else ">") }
                CompactButton(onClick = onNext) { Text(">|") }
            }
            Row(
                modifier = Modifier.padding(top = 4.dp),
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                CompactButton(onClick = { onVolume(-1) }) { Text("−") }
                Text(
                    text = state.volume?.toString() ?: "—",
                    modifier = Modifier.width(36.dp),
                    textAlign = TextAlign.Center,
                    style = MaterialTheme.typography.body1,
                )
                CompactButton(onClick = { onVolume(1) }) { Text("+") }
            }
        }
    }
}
