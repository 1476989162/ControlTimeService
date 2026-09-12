package com.controltime.android.network

import com.controltime.android.model.*
import com.google.gson.Gson
import com.google.gson.reflect.TypeToken
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.util.concurrent.TimeUnit

/**
 * OkHttp API 客户端实现
 */
class ApiClient(
    private val serverUrl: String,
    private val gson: Gson = Gson()
) : ApiService {

    private val client = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(10, TimeUnit.SECONDS)
        .writeTimeout(10, TimeUnit.SECONDS)
        .retryOnConnectionFailure(true)
        .build()

    private val jsonMediaType = "application/json; charset=utf-8".toMediaType()

    private fun buildUrl(path: String): String {
        return "${serverUrl.trimEnd('/')}$path"
    }

    override suspend fun register(client: ClientInfo): String? = withContext(Dispatchers.IO) {
        try {
            val body = gson.toJson(client).toRequestBody(jsonMediaType)
            val request = Request.Builder()
                .url(buildUrl("/api/clients/register"))
                .post(body)
                .build()
            val response = client.newCall(request).execute()
            if (response.isSuccessful) response.body?.string() else null
        } catch (e: Exception) {
            null
        }
    }

    override suspend fun heartbeat(id: String, name: String): String? = withContext(Dispatchers.IO) {
        try {
            val body = gson.toJson(mapOf("id" to id, "name" to name)).toRequestBody(jsonMediaType)
            val request = Request.Builder()
                .url(buildUrl("/api/clients/heartbeat"))
                .post(body)
                .build()
            val response = client.newCall(request).execute()
            if (response.isSuccessful) response.body?.string() else null
        } catch (e: Exception) {
            null
        }
    }

    override suspend fun getCommands(clientId: String): List<RemoteCommand>? = withContext(Dispatchers.IO) {
        try {
            val encodedId = java.net.URLEncoder.encode(clientId, "UTF-8")
            val request = Request.Builder()
                .url(buildUrl("/api/clients/$encodedId/commands"))
                .get()
                .build()
            val response = client.newCall(request).execute()
            if (response.isSuccessful) {
                val json = response.body?.string()
                if (json != null && json != "[]") {
                    val type = object : TypeToken<List<RemoteCommand>>() {}.type
                    gson.fromJson(json, type)
                } else null
            } else null
        } catch (e: Exception) {
            null
        }
    }

    override suspend fun updateStatus(clientId: String, status: StatusUpdate): String? = withContext(Dispatchers.IO) {
        try {
            val encodedId = java.net.URLEncoder.encode(clientId, "UTF-8")
            val body = gson.toJson(status).toRequestBody(jsonMediaType)
            val request = Request.Builder()
                .url(buildUrl("/api/clients/$encodedId/status"))
                .post(body)
                .build()
            val response = client.newCall(request).execute()
            if (response.isSuccessful) response.body?.string() else null
        } catch (e: Exception) {
            null
        }
    }

    override suspend fun sendMessage(clientId: String, message: MessageRequest): String? = withContext(Dispatchers.IO) {
        try {
            val encodedId = java.net.URLEncoder.encode(clientId, "UTF-8")
            val body = gson.toJson(message).toRequestBody(jsonMediaType)
            val request = Request.Builder()
                .url(buildUrl("/api/clients/$encodedId/message"))
                .post(body)
                .build()
            val response = client.newCall(request).execute()
            if (response.isSuccessful) response.body?.string() else null
        } catch (e: Exception) {
            null
        }
    }

    override suspend fun getClientInfo(clientId: String): ClientInfo? = withContext(Dispatchers.IO) {
        try {
            val encodedId = java.net.URLEncoder.encode(clientId, "UTF-8")
            val request = Request.Builder()
                .url(buildUrl("/api/clients/$encodedId"))
                .get()
                .build()
            val response = client.newCall(request).execute()
            if (response.isSuccessful) {
                val json = response.body?.string()
                if (json != null) gson.fromJson(json, ClientInfo::class.java) else null
            } else null
        } catch (e: Exception) {
            null
        }
    }
}
