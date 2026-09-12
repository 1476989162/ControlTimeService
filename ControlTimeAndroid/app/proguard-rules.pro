# Add project specific ProGuard rules here.
# By default, the flags in this file are appended to flags specified
# in Android\sdk/tools/proguard/proguard-android.txt
# You can edit the include path and order by changing the proguardFiles
# directive in build.gradle.
#
# For more details, see
#   http://developer.android.com/guide/developing/tools/proguard.html

# Keep model classes
-keep class com.controltime.android.model.** { *; }

# Keep Room entities
-keep class com.controltime.android.data.** { *; }

# Gson
-keepattributes Signature
-keepattributes *Annotation*
-keep class com.google.gson.** { *; }

# OkHttp
-dontwarn okhttp3.**
-dontwarn okio.**
