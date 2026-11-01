package app.lanbridge.protocol

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class HistoryUiTest {
    @Test
    fun directions_are_labelled_from_the_phone_point_of_view() {
        assertEquals("来自手机", HistoryUi.originOnPhone("out"))
        assertEquals("来自手机", HistoryUi.originOnPhone("to_pc"))
        assertEquals("来自电脑", HistoryUi.originOnPhone("in"))
        assertEquals("来自电脑", HistoryUi.originOnPhone("to_phone"))
    }

    @Test
    fun legacy_rows_stay_visible_for_a_single_pc_only() {
        assertTrue(HistoryUi.visible(null, "pc-a", 1))
        assertFalse(HistoryUi.visible(null, "pc-a", 2))
        assertTrue(HistoryUi.visible("pc-b", "pc-b", 2))
        assertFalse(HistoryUi.visible("pc-a", "pc-b", 2))
        assertTrue(HistoryUi.canCopy("clipboard"))
        assertFalse(HistoryUi.canCopy("file"))
        assertTrue(HistoryUi.canOpen("file", "content://downloads/1"))
        assertFalse(HistoryUi.canOpen("file", null))
    }
}
