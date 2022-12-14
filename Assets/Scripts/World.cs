using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Threading;
using System.IO;

public class World : MonoBehaviour {
    public Settings settings;

    [Header("World Gen Values")]
    public BiomeAttributes[] biomes;

    [Range(0f, 1f)]
    public float globalLightLevel;
    public Color day;
    public Color night;

    public Transform player;
    public Vector3 spawn;

    public Material material;
    public Material transparentMaterial;
    public BlockType[] blocktypes = {
        new BlockType()
    };

    Chunk[,] chunks = new Chunk[VoxelData.WorldSizeInChunks, VoxelData.WorldSizeInChunks];

    List<ChunkCoord> activeChunks = new List<ChunkCoord>();
    public ChunkCoord playerChunkCoord;
    ChunkCoord playerLastChunkCoord;

    List<ChunkCoord> chunksToCreate = new List<ChunkCoord>();
    public List<Chunk> chunksToUpdate = new List<Chunk>();
    public Queue<Chunk> chunksToDraw = new Queue<Chunk>();

    bool applyingModifications = false;
    //private bool isCreatingChunks;

    Queue<Queue<VoxelMod>> modifications = new Queue<Queue<VoxelMod>>();

    public GameObject debugScreen;
    public GameObject creativeInventoryWindow;
    public GameObject cursorSlot;


    /*
    notes about threading
     1. unity does not allow you to draw game objects on background threads
     2. threading errors and logs will not show up in the console
    */
    /*
     ! A better threading solution is to use the Unity Jobs System.
     This will require a lot of code changes but is generally considered a better way of handling the situation. As such this should be saved till the end of the build process.

     https://docs.unity3d.com/Manual/JobSystem.html
    */
    Thread ChunkUpdateThread;
    public object ChunkUpdateThreadLock = new object();

    private bool _inUI = false;
    public bool inUI {
        get {return _inUI;}
        set {
            _inUI = value;
            if (_inUI) {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                creativeInventoryWindow.SetActive(true);
                cursorSlot.SetActive(true);
            } else {
                Cursor.lockState = CursorLockMode.Locked;  
                Cursor.visible = false;
                creativeInventoryWindow.SetActive(false);
                cursorSlot.SetActive(false);
            }
        }
    }

    private void Start() {
        //string jsonExport = JsonUtility.ToJson(settings);
        //File.WriteAllText(Application.dataPath + "/settings.cfg", jsonExport);
        string jsonImport = File.ReadAllText(Application.dataPath + "/settings.cfg");
        settings = JsonUtility.FromJson<Settings>(jsonImport);

        Random.InitState(settings.seed);

        Shader.SetGlobalFloat("minGlobalLightLevel", VoxelData.minLightLevel);
        Shader.SetGlobalFloat("maxGlobalLightLevel", VoxelData.maxLightLevel);

        if (settings.enableThreading) {
            ChunkUpdateThread = new Thread(new ThreadStart(ThreadedUpdate));
            ChunkUpdateThread.Start();
        }

        SetGlobalLightValue();
        spawn = new Vector3((VoxelData.WorldSizeInChunks * VoxelData.ChunkWidth) / 2f, VoxelData.ChunkHeight - 50f, (VoxelData.WorldSizeInChunks * VoxelData.ChunkWidth) / 2f);
        GenerateWorld();
        playerChunkCoord = GetChunkCoordFromVector3(player.position);
    }

    public void SetGlobalLightValue() {
        Shader.SetGlobalFloat("GlobalLightLevel", globalLightLevel);
        Camera.main.backgroundColor = Color.Lerp(night, day, globalLightLevel);
    }

    private void Update() {
        playerChunkCoord = GetChunkCoordFromVector3(player.position);

        if (!playerChunkCoord.Equals(playerLastChunkCoord))
            CheckViewDistance();
        if (chunksToCreate.Count > 0)
            CreateChunk();
        
        if (chunksToDraw.Count > 0)
            if(chunksToDraw.Peek().isEditable)
                chunksToDraw.Dequeue().CreateMesh();
        
        if (!settings.enableThreading) {
            if (!applyingModifications)
                ApplyModifications();
            if (chunksToUpdate.Count > 0)
                UpdateChunks();
        }

        if (Input.GetKeyDown(KeyCode.F3))
            debugScreen.SetActive(!debugScreen.activeSelf);
    }

    void GenerateWorld() {
        for (int x = (VoxelData.WorldSizeInChunks / 2) - settings.viewDistance; x < (VoxelData.WorldSizeInChunks / 2) + settings.viewDistance; x++) {
            for (int z = (VoxelData.WorldSizeInChunks / 2) - settings.viewDistance; z < (VoxelData.WorldSizeInChunks / 2) + settings.viewDistance; z++) {
                ChunkCoord newChunk = new ChunkCoord(x, z);
                chunks[x, z] = new Chunk(newChunk, this);
                chunksToCreate.Add(newChunk);
            }
        }

        player.position = spawn;
        CheckViewDistance();
    }

    void CreateChunk () {
        ChunkCoord c = chunksToCreate[0];
        chunksToCreate.RemoveAt(0);
        chunks[c.x, c.z].Init();
    }

    void UpdateChunks() {
        bool updated = false;
        int index = 0;

        lock(ChunkUpdateThreadLock) {
            while(!updated && index < chunksToUpdate.Count - 1) {
                if(chunksToUpdate[index].isEditable) {
                    chunksToUpdate[index].UpdateChunk();
                    if (!activeChunks.Contains(chunksToUpdate[index].coord))
                        activeChunks.Add(chunksToUpdate[index].coord);
                    chunksToUpdate.RemoveAt(index);
                    updated = true;
                } else
                    index++;
            }
        }
    }

    void ThreadedUpdate() {
        while(true) {
            if (!applyingModifications)
                ApplyModifications();
            if (chunksToUpdate.Count > 0)
                UpdateChunks();
        }
    }

    private void OnDisable() {
        if (settings.enableThreading) 
            ChunkUpdateThread.Abort();
    }

    // this is a coroutine that can pause and start back up as opposed to tanking your system
    void ApplyModifications() {
        applyingModifications = true;

        while(modifications.Count > 0) {
            Queue<VoxelMod> queue = modifications.Dequeue();
            while(queue.Count > 0) {
                VoxelMod v = queue.Dequeue();
                ChunkCoord c = GetChunkCoordFromVector3(v.position);

                if (chunks[c.x, c.z] == null) {
                    chunks[c.x, c.z] = new Chunk(c, this);
                    chunksToCreate.Add(c);
                }

                chunks[c.x, c.z].modifications.Enqueue(v);
            }
        }
        applyingModifications = false;
    }

    ChunkCoord GetChunkCoordFromVector3 (Vector3 pos) {
        int x = Mathf.FloorToInt(pos.x / VoxelData.ChunkWidth);
        int z = Mathf.FloorToInt(pos.z / VoxelData.ChunkWidth);
        return new ChunkCoord(x, z);
    }

    public Chunk GetChunkFromVector3 (Vector3 pos) {
        int x = Mathf.FloorToInt(pos.x / VoxelData.ChunkWidth);
        int z = Mathf.FloorToInt(pos.z / VoxelData.ChunkWidth);
        return chunks[x, z];
    }

    void CheckViewDistance() {
        ChunkCoord coord= GetChunkCoordFromVector3(player.position);
        playerLastChunkCoord = playerChunkCoord;
        List<ChunkCoord> previouslyActiveChunks= new List<ChunkCoord>(activeChunks);
        activeChunks.Clear();

        // Loop through all chunks currently within view distance of the player.
        for (int x= coord.x- settings.viewDistance; x< coord.x+ settings.viewDistance; x++) {
            for (int z= coord.z- settings.viewDistance; z< coord.z + settings.viewDistance; z++) {
                ChunkCoord c = new ChunkCoord(x, z);                             
                // If the current chunk is in the world...
                if (IsChunkInWorld(c)) {
                    // Check if it active, if not, activate it.
                    if (chunks[x, z] == null) {
                        chunks[x, z] = new Chunk(c, this);
                        chunksToCreate.Add(c);
                    } else if (!chunks[x, z].isActive) {
                        chunks[x, z].isActive= true;
                    }

                    activeChunks.Add(c);
                }

                // Check through previously active chunks to see if this chunk is there. If it is, remove it from the list.
                for (int i= 0; i< previouslyActiveChunks.Count; i++) {
                    if (previouslyActiveChunks[i].Equals(c))
                        previouslyActiveChunks.RemoveAt(i);
                }
            }
        }

        // Any chunks left in the previousActiveChunks list are no longer in the player's view distance, so loop through and disable them.
        foreach (ChunkCoord c in previouslyActiveChunks)
            chunks[c.x, c.z].isActive= false;
    }

    // our xyz is the bottom left corner of our chunk
    public bool CheckForVoxel (Vector3 pos) {
        ChunkCoord chunk = new ChunkCoord(pos);

        if (!IsChunkInWorld(chunk) || pos.y < 0 || pos.y > VoxelData.ChunkHeight)
            return false;

        if (chunks[chunk.x, chunk.z] != null && chunks[chunk.x, chunk.z].isEditable)
            return blocktypes[chunks[chunk.x, chunk.z].GetVoxelFromGlobalVector3(pos).id].isSolid;

        return blocktypes[GetVoxel(pos)].isSolid;
    }

    public VoxelState GetVoxelState (Vector3 pos) {
        ChunkCoord chunk = new ChunkCoord(pos);

        if (!IsChunkInWorld(chunk) || pos.y < 0 || pos.y > VoxelData.ChunkHeight)
            return null;

        if (chunks[chunk.x, chunk.z] != null && chunks[chunk.x, chunk.z].isEditable)
            return chunks[chunk.x, chunk.z].GetVoxelFromGlobalVector3(pos);

        return new VoxelState(GetVoxel(pos));
    }

    public byte GetVoxel (Vector3 pos) {
        // -!- immutable pass -!-
        // If outside world, return air.
        if (!IsVoxelInWorld(pos))
            return 0;
        
        // bottom block of the chunk, return bedrock
        int yAbs = Mathf.FloorToInt(pos.y);
        if (yAbs == 0)
            return 8;

        // biome selection pass
        int solidGroundHeight = 42;
        float sumOfHeights = 0f;
        int count = 0;
        float strongestWeight = 0f;
        int strongestBiomeIndex = 0;

        for (int i = 0; i < biomes.Length; i++) {
            float weight = Noise.Get2DPerlin(new Vector2(pos.x, pos.z), biomes[i].offset, biomes[i].scale);

            // strongest weight
            if (weight > strongestWeight) {
                strongestWeight = weight;
                strongestBiomeIndex = i;
            }

            // current height
            float height = biomes[i].terrainHeight* Noise.Get2DPerlin(new Vector2(pos.x, pos.z), 0, biomes[i].terrainScale)* weight;
            if (height > 0) {
                sumOfHeights += height;
                count++;
            }
        }

        BiomeAttributes biome = biomes[strongestBiomeIndex];
        sumOfHeights /= count; // avg the heights
        
        int terrainHeight = Mathf.FloorToInt(sumOfHeights + solidGroundHeight);

        // -!- basic terrain pass -!-
        byte voxelValue = 0;
        if (yAbs == terrainHeight)
            voxelValue= biome.surfaceBlock; // grass
        else if (yAbs< terrainHeight && yAbs> terrainHeight- 4)
            voxelValue= biome.subSurfaceBlock; // dirt
        else if (yAbs> terrainHeight)
            return 0; // air
        else
            voxelValue= biome.sedimentaryBlock; // stone/sandstone

        // -!- second pass :: ores -!-
        if (voxelValue == 1) {
            foreach (Lode lode in biome.lodes) {
                if (yAbs> lode.minHeight && yAbs< lode.maxHeight)
                    if (Noise.Get3DPerlin(pos, lode.noiseOffset, lode.scale, lode.threshold))
                        voxelValue = lode.blockID;
            }
        }

        // -!- major flora pass -!-
        if (yAbs == terrainHeight && biome.placeMajorFlora) {
            if (Noise.Get2DPerlin(new Vector2(pos.x, pos.z), 0, biome.majorFloraZoneScale) > biome.majorFloraZoneThreshold)
                if (Noise.Get2DPerlin(new Vector2(pos.x, pos.z), 0, biome.majorFloraPlacementScale) > biome.majorFloraPlacementThreshold)
                    modifications.Enqueue(Structure.GenerateMajorFlora(biome.majorFloraIndex, pos, biome.minHeight, biome.maxHeight));
        }

        return voxelValue;
    }

    bool IsChunkInWorld(ChunkCoord coord) {
        if (coord.x> 0 && coord.x< VoxelData.WorldSizeInChunks-1 && 
            coord.z> 0 && coord.z< VoxelData.WorldSizeInChunks-1)
            return true;
        return false;
    }

    bool IsVoxelInWorld (Vector3 pos) {
        if (pos.x>= 0 && pos.x< VoxelData.WorldSizeInVoxels && 
            pos.y>= 0 && pos.y< VoxelData.ChunkHeight && 
            pos.z>= 0 && pos.z< VoxelData.WorldSizeInVoxels)
            return true;
        return false;
    }
}

[System.Serializable]
public class BlockType {
    public string blockName;
    public bool isSolid;
    public bool renderNeighborFaces;
    public float transparency;
    public Sprite icon;

    [Header("Texture Values")]
    public int backXFaceTexture;
    public int backYFaceTexture;
    public int backFaceTexture {
        get { return backXFaceTexture*16 + backYFaceTexture; }
    }

    public int frontXFaceTexture;
    public int frontYFaceTexture;
    public int frontFaceTexture {
        get { return frontXFaceTexture*16 + frontYFaceTexture; }
    }

    public int topXFaceTexture;
    public int topYFaceTexture;
    public int topFaceTexture {
        get { return topXFaceTexture*16 + topYFaceTexture; }
    }

    public int bottomXFaceTexture;
    public int bottomYFaceTexture;
    public int bottomFaceTexture {
        get { return bottomXFaceTexture*16 + bottomYFaceTexture; }
    }

    public int leftXFaceTexture;
    public int leftYFaceTexture;
    public int leftFaceTexture {
        get { return leftXFaceTexture*16 + leftYFaceTexture; }
    }
    public int rightXFaceTexture;
    public int rightYFaceTexture;
    public int rightFaceTexture {
        get { return rightXFaceTexture*16 + rightYFaceTexture; }
    }

    // Back, Front, Top, Bottom, Left, Right
    public int GetTextureID (int faceIndex) {
        switch (faceIndex) {
            case 0:
                return backFaceTexture;
            case 1:
                return frontFaceTexture;
            case 2:
                return topFaceTexture;
            case 3:
                return bottomFaceTexture;
            case 4:
                return leftFaceTexture;
            case 5:
                return rightFaceTexture;
            default:
                Debug.Log("Error in GetTextureID; invalid face index");
                return 0;
        }
    }
}


public class VoxelMod {
    public Vector3 position;
    public byte id;

    public VoxelMod() {
        position = new Vector3();
        id = 0;
    }

    public VoxelMod(Vector3 _pos, byte _id) {
        position = _pos;
        id = _id;
    }
}

[System.Serializable]
public class Settings {
    [Header("Game Data")]
    public string version;

    [Header("Performance")]
    public int viewDistance;
    public bool enableThreading;
    public bool enableAnimatedChunks;

    [Header("Controls")]
    [Range(0.1f, 10f)]
    public float mouseSensitivity; 

    [Header("World Gen Values")]
    public int seed;
}