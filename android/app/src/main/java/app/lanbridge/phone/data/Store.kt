package app.lanbridge.phone.data

import android.content.Context
import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Room
import androidx.room.RoomDatabase
import java.util.UUID

@Entity(tableName = "peers")
data class PeerEntity(
    @PrimaryKey val deviceId: String,
    val name: String,
    val fingerprint: String,
    val host: String,
    val port: Int,
    val pairedAt: Long
)

@Entity(tableName = "transfers")
data class TransferEntity(
    @PrimaryKey val id: String,
    val direction: String,
    val kind: String,
    val name: String,
    val size: Long,
    val status: String,
    val createdAt: Long,
    val preview: String?
)

@Dao
interface PeerDao {
    @Query("SELECT * FROM peers LIMIT 1")
    suspend fun current(): PeerEntity?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun save(peer: PeerEntity)

    @Query("DELETE FROM peers")
    suspend fun clear()
}

@Dao
interface TransferDao {
    @Query("SELECT * FROM transfers ORDER BY createdAt DESC LIMIT 50")
    suspend fun recent(): List<TransferEntity>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(item: TransferEntity)

    @Query("DELETE FROM transfers")
    suspend fun clear()
}

@Database(entities = [PeerEntity::class, TransferEntity::class], version = 1, exportSchema = false)
abstract class AppDb : RoomDatabase() {
    abstract fun peers(): PeerDao
    abstract fun transfers(): TransferDao

    companion object {
        @Volatile private var instance: AppDb? = null
        fun get(context: Context): AppDb = instance ?: synchronized(this) {
            instance ?: Room.databaseBuilder(context.applicationContext, AppDb::class.java, "lanbridge.db")
                .build().also { instance = it }
        }
    }
}

class Prefs(context: Context) {
    private val sp = context.getSharedPreferences("lanbridge", Context.MODE_PRIVATE)

    fun deviceId(): String {
        val existing = sp.getString("deviceId", null)
        if (!existing.isNullOrBlank()) return existing
        val created = UUID.randomUUID().toString()
        sp.edit().putString("deviceId", created).apply()
        return created
    }

    var deviceName: String
        get() = sp.getString("deviceName", null) ?: android.os.Build.MODEL
        set(value) { sp.edit().putString("deviceName", value).apply() }

    var autoReceive: Boolean
        get() = sp.getBoolean("autoReceive", true)
        set(value) { sp.edit().putBoolean("autoReceive", value).apply() }

    var saveHistory: Boolean
        get() = sp.getBoolean("saveHistory", true)
        set(value) { sp.edit().putBoolean("saveHistory", value).apply() }

    var filterSensitive: Boolean
        get() = sp.getBoolean("filterSensitive", true)
        set(value) { sp.edit().putBoolean("filterSensitive", value).apply() }
}
