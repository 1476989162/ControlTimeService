package com.controltime.android.data

import androidx.room.*
import java.util.Calendar

/**
 * 使用记录 DAO
 */
@Dao
interface UsageRecordDao {
    
    @Query("SELECT * FROM usage_record WHERE date = :date")
    suspend fun getByDate(date: String): List<UsageRecordEntity>
    
    @Query("SELECT * FROM usage_record WHERE packageName = :packageName AND date = :date")
    suspend fun getByPackageAndDate(packageName: String, date: String): UsageRecordEntity?
    
    @Insert
    suspend fun insert(record: UsageRecordEntity)
    
    @Update
    suspend fun update(record: UsageRecordEntity)
    
    @Query("SELECT SUM(usageSeconds) FROM usage_record WHERE date = :date")
    suspend fun getTotalUsageSeconds(date: String): Long?
}

/**
 * 客户端信息 DAO
 */
@Dao
interface ClientInfoDao {
    
    @Query("SELECT * FROM client_info LIMIT 1")
    suspend fun get(): ClientInfoEntity?
    
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsert(client: ClientInfoEntity)
    
    @Query("DELETE FROM client_info")
    suspend fun deleteAll()
}
