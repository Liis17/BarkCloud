package com.barkfluff.BarkCloud

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import com.barkfluff.BarkCloud.ui.navigation.RootNavGraph
import com.barkfluff.BarkCloud.ui.theme.BarkCloudTheme

class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        setContent {
            BarkCloudTheme {
                RootNavGraph()
            }
        }
    }
}
