# AudioClone - 强大的音频捕获、重复和串流工具 A powerful Audio Capturing, Repeating and Streaming Utility 
AudioClone是一个强大的音频捕获、重复和串流工具，它还是AudioClone等软件的重要组件。

# AudioClone 是如何工作的
下面是一个简化的工作流程。
```mermaid
graph TD
    A1[Application 1] -- DirectSound--> DS[DirectSound API]
    A4[Application 2] -- DirectSound--> DS
    A2[System Sounds] -- DirectSound--> DS
    DS -- WASAPI shared mode--> B[WASAPI render client]
    A3[Application 3] -- WASAPI shared mode--> B
    B --> C[Audio engine]
    C -- Loopback source----> G[Loopback data offload]
    C ---> D[System Mixer]
    
    
    subgraph Capture Application
    G --> H[WasapiLoopbackCapture]
    H --> I[PCM Stream provider]
    end

    subgraph User mode
    A1
    A2
    A3
    A4
    DS
    B
    C
    D
    H
    I
    end


    subgraph Kernel mode
    D --> E[Audio driver stack]
    end


    subgraph Endpoint device
    E --> F[Hardware Decoder/DAC]


    end


    subgraph AudioClone
    I -- Register a stream--> S[Audio stream]
    S --> K["Codec<br>(MP3,WAV,FLAC)"]
    S -- RAW PCM data --> M
    K --> M["AudioClone.Server<br>(via ASP.NET Web server)"]

    M --> N[HTTP Endpoint]
    I -- Register a stream--> S1[Audio stream]


    S1 --> Z[AudioClone<br>Loopback recorder]
    Z --> Z2["Codec<br>(MP3,WAV,FLAC)"]
    

    I -- Register a stream--> S2[Audio stream]

    S2 --> V[AudioClone Repeater]
    V -->V1[AudioClone Repeater Player]
    
    
    end

    N --> M1[Another devices]
    Z2 ---> File
    V1 -- WASAPI Shared mode---> V2[Another endpoint device]
    F -- Analogue/Digital signal-------> X[Speaker, earphones, TV speaker...]


```

请注意`AudioClone`功能目前完全尚未实现，会在后续的版本中完善。


# 许可证

基于[Apache License](LICENSE.txt)开源