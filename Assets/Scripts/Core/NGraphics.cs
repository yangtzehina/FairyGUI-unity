using System;
using System.Collections.Generic;
using UnityEngine;
using FairyGUI.Utils;
using Object = UnityEngine.Object;

namespace FairyGUI
{
    /// <summary>
    /// 
    /// </summary>
    public class NGraphics : IMeshFactory, IBatchable
    {
        /// <summary>
        /// 
        /// </summary>
        public GameObject gameObject { get; private set; }

        /// <summary>
        /// 
        /// </summary>
        public MeshFilter meshFilter { get; private set; }

        /// <summary>
        /// 
        /// </summary>
        public MeshRenderer meshRenderer { get; private set; }

        /// <summary>
        /// 
        /// </summary>
        public Mesh mesh { get; private set; }

        BlendMode _blendMode;

        /// <summary>
        /// Non-Normal blend modes keep the native renderer (the instanced stream's
        /// blend state is fixed), so changes must push a membership re-evaluation.
        /// A leaf that was never claimed and later returns to Normal simply stays
        /// on the native path — correct, just unbatched.
        /// </summary>
        public BlendMode blendMode
        {
            get { return _blendMode; }
            set
            {
                if (_blendMode != value)
                {
                    _blendMode = value;
                    var s = _instancedBy != null ? _instancedBy : _lastInstancedBy;
                    if (s != null)
                        s._MarkStructureDirty();
                }
            }
        }

        /// <summary>
        /// 不参与剪裁
        /// </summary>
        public bool dontClip;

        /// <summary>
        /// 当Mesh更新时触发
        /// </summary>
        public event Action meshModifier;

        /// <summary>
        /// 
        /// </summary>
        public List<NGraphics> subInstances;

        /// <summary>
        /// 
        /// </summary>
        public Vector4 userData;

        NTexture _texture;
        string _shader;
        Material _material;
        int _customMatarial; //0-none, 1-common, 2-support internal mask, 128-owns material
        MaterialManager _manager;
        string[] _shaderKeywords;
        int _materialFlags;
        IMeshFactory _meshFactory;

        float _alpha;
        Color _color;
        bool _meshDirty;
        Rect _contentRect;
        FlipType _flip;
        int _renderingOrder;

        MaterialManager.MaterialRef _matRef;
        MaterialManager _matRefOwner;
        int _matRefFlags;

        //incremented whenever the mesh content is mutated (rebuild/alpha/tint),
        //so a MergedBatch can detect changes with a managed int compare
        internal int _contentVersion;

        //the instanced stream currently rendering this leaf in place of the native
        //renderer. The mark lives on the LEAF: dispose/reparent clears it from the
        //leaf side so recovery never depends on the batch's lifecycle.
        internal InstancedUIStream _instancedBy;
        //curve text (batch 5): CurveBaseFont mirrors each built glyph quad here
        //during StartDraw/DrawGlyph — the instanced stream emits analytic curve
        //quads from this table instead of reassembling the encoded native mesh
        internal System.Collections.Generic.List<CurveTextMesh.GlyphQuad> _curveGlyphs;

        //color tier (batch 3): claimed leaves defer native mesh color rewrites
        //(the renderer is off; the stream pushes instance colors directly);
        //_RestoreNativeColors settles the debt on release
        internal bool _colorStale;
        internal bool _tintStale;
        //kept after release so re-admission conditions (enabled=true) can still
        //reach a stream; stale marks only cost one redundant recompile
        internal InstancedUIStream _lastInstancedBy;

        internal void _SetInstancedOwner(InstancedUIStream stream)
        {
            _instancedBy = stream;
            _lastInstancedBy = stream;
            if (meshRenderer != null)
                meshRenderer.forceRenderingOff = true;
        }

        internal void _ClearInstancedOwner()
        {
            _instancedBy = null;
            _RestoreNativeColors();
            if (meshRenderer != null)
            {
                meshRenderer.forceRenderingOff = false;
                //resync the native sortingOrder skipped while claimed
                meshRenderer.sortingOrder = _renderingOrder;
            }
        }

        /// <summary>
        /// Settles color writes deferred while the leaf was claimed (color tier,
        /// batch 3): reapplies the native formula col.a = alpha * backup — plus
        /// rgb = color when a Tint was deferred — so the mesh is correct the
        /// moment native rendering resumes or the stream re-reads it.
        /// </summary>
        internal void _RestoreNativeColors()
        {
            if (!_colorStale)
                return;
            bool tint = _tintStale;
            _colorStale = _tintStale = false;
            if (_meshDirty || mesh == null)
                return; //the pending rebuild bakes fresh colors anyway

            int vertCount = mesh.vertexCount;
            if (vertCount == 0)
                return;
            VertexBuffer vb = VertexBuffer.Begin();
            mesh.GetColors(vb.colors);
            List<Color32> colors = vb.colors;
            for (int i = 0; i < vertCount; i++)
            {
                Color32 col = tint ? (Color32)_color : colors[i];
                col.a = (byte)(_alpha * (hasAlphaBackup ? _alphaBackup[i] : (byte)255));
                colors[i] = col;
            }
            mesh.SetColors(vb.colors);
            vb.End();
        }

        internal bool hasPropertyBlock
        {
            get { return _propertyBlock != null; }
        }

        //SDF leaf emission (M7) reproduces mesh colors without reading the mesh
        internal Color _tintColor { get { return _color; } }
        internal float _currentAlpha { get { return _alpha; } }

        public class VertexMatrix
        {
            public Vector3 cameraPos;
            public Matrix4x4 matrix;
        }
        VertexMatrix _vertexMatrix;

        bool hasAlphaBackup;
        List<byte> _alphaBackup; //透明度改变需要通过修改顶点颜色实现，但顶点颜色本身可能就带有透明度，所以这里要有一个备份

        internal int _maskFlag;
        StencilEraser _stencilEraser;

        MaterialPropertyBlock _propertyBlock;
        bool _blockUpdated;

        internal BatchElement _batchElement;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="gameObject"></param>
        public NGraphics(GameObject gameObject)
        {
            this.gameObject = gameObject;

            _alpha = 1f;
            _shader = ShaderConfig.imageShader;
            _color = Color.white;
            _meshFactory = this;

            meshFilter = gameObject.AddComponent<MeshFilter>();
            meshRenderer = gameObject.AddComponent<MeshRenderer>();
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            meshRenderer.receiveShadows = false;

            mesh = new Mesh();
            mesh.name = gameObject.name;
            mesh.MarkDynamic();

            meshFilter.mesh = mesh;

            meshFilter.hideFlags = DisplayObject.hideFlags;
            meshRenderer.hideFlags = DisplayObject.hideFlags;
            mesh.hideFlags = DisplayObject.hideFlags;

            Stats.LatestGraphicsCreation++;
        }

        /// <summary>
        /// 
        /// </summary>
        public IMeshFactory meshFactory
        {
            get { return _meshFactory; }
            set
            {
                if (_meshFactory != value)
                {
                    _meshFactory = value;
                    _meshDirty = true;
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public T GetMeshFactory<T>() where T : IMeshFactory, new()
        {
            if (!(_meshFactory is T))
            {
                _meshFactory = new T();
                _meshDirty = true;
            }
            return (T)_meshFactory;
        }

        /// <summary>
        /// 
        /// </summary>
        public Rect contentRect
        {
            get { return _contentRect; }
            set
            {
                _contentRect = value;
                _meshDirty = true;

                if (subInstances != null)
                {
                    foreach (var sub in subInstances)
                        sub.contentRect = value;
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public FlipType flip
        {
            get { return _flip; }
            set
            {
                if (_flip != value)
                {
                    _flip = value;
                    _meshDirty = true;
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public NTexture texture
        {
            get { return _texture; }
            set
            {
                if (_texture != value)
                {
                    if (value != null)
                        value.AddRef();
                    if (_texture != null)
                        _texture.ReleaseRef();

                    _texture = value;
                    if (_customMatarial != 0 && _material != null)
                        _material.mainTexture = _texture != null ? _texture.nativeTexture : null;
                    _meshDirty = true;
                    UpdateManager();
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public string shader
        {
            get { return _shader; }
            set
            {
                _shader = value;
                UpdateManager();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="shader"></param>
        /// <param name="texture"></param>
        public void SetShaderAndTexture(string shader, NTexture texture)
        {
            _shader = shader;
            if (_texture != texture)
                this.texture = texture;
            else
                UpdateManager();
        }

        /// <summary>
        /// 
        /// </summary>
        public Material material
        {
            get
            {
                if (_customMatarial == 0 && _material == null && _manager != null)
                    _material = _manager.GetMaterial(_materialFlags, blendMode, 0);
                return _material;
            }
            set
            {
                if ((_customMatarial & 128) != 0 && _material != null)
                    Object.DestroyImmediate(_material);

                _material = value;
                if (_material != null)
                {
                    _customMatarial = 1;
                    if (_material.HasProperty(ShaderConfig.ID_Stencil) || _material.HasProperty(ShaderConfig.ID_ClipBox))
                        _customMatarial |= 2;

                    meshRenderer.sharedMaterial = _material;
                    if (_texture != null)
                        _material.mainTexture = _texture.nativeTexture;
                }
                else
                {
                    _customMatarial = 0;
                    meshRenderer.sharedMaterial = null;
                }
            }
        }

        /// <summary>
        /// Same as material property except that ownership is transferred to this object.
        /// </summary>
        /// <param name="material"></param>
        public void SetMaterial(Material material)
        {
            this.material = material;
            _customMatarial |= 128;
        }

        /// <summary>
        /// 
        /// </summary>
        public string[] materialKeywords
        {
            get { return _shaderKeywords; }
            set
            {
                _shaderKeywords = value;
                UpdateMaterialFlags();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="keyword"></param>
        /// <param name="enabled"></param>
        public void ToggleKeyword(string keyword, bool enabled)
        {
            if (enabled)
            {
                if (_shaderKeywords == null)
                {
                    _shaderKeywords = new string[] { keyword };
                    UpdateMaterialFlags();
                }
                else if (Array.IndexOf(_shaderKeywords, keyword) == -1)
                {
                    Array.Resize(ref _shaderKeywords, _shaderKeywords.Length + 1);
                    _shaderKeywords[_shaderKeywords.Length - 1] = keyword;
                    UpdateMaterialFlags();
                }
            }
            else
            {
                if (_shaderKeywords != null)
                {
                    int i = Array.IndexOf(_shaderKeywords, keyword);
                    if (i != -1)
                    {
                        _shaderKeywords[i] = null;
                        UpdateMaterialFlags();
                    }
                }
            }
        }

        void UpdateManager()
        {
            if (_texture != null)
                _manager = _texture.GetMaterialManager(_shader);
            else
                _manager = null;
            UpdateMaterialFlags();
        }

        void UpdateMaterialFlags()
        {
            if (_customMatarial != 0)
            {
                if (material != null)
                    material.shaderKeywords = _shaderKeywords;
            }
            else if (_shaderKeywords != null && _manager != null)
                _materialFlags = _manager.GetFlagsByKeywords(_shaderKeywords);
            else
                _materialFlags = 0;
        }

        /// <summary>
        /// 
        /// </summary>
        public bool enabled
        {
            get { return meshRenderer.enabled; }
            set
            {
                if (meshRenderer.enabled != value)
                {
                    meshRenderer.enabled = value;
                    //enabled is an instancing admission condition (review M10/M14):
                    //push so the stream re-evaluates membership; after a release
                    //only _lastInstancedBy still knows who might re-admit us
                    var s = _instancedBy != null ? _instancedBy : _lastInstancedBy;
                    if (s != null)
                        s._MarkStructureDirty();
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        [Obsolete("Use renderingOrder")]
        public int sortingOrder
        {
            get { return _renderingOrder; }
            set { renderingOrder = value; }
        }

        /// <summary>
        ///
        /// </summary>
        public int renderingOrder
        {
            get { return _renderingOrder; }
            set
            {
                //writing to meshRenderer.sortingOrder is a native call, skip it when unchanged
                if (_renderingOrder != value)
                {
                    _renderingOrder = value;
                    //claimed leaves keep the managed field fresh (ComputeRunOrders
                    //reads it) but skip the native write — the renderer is off;
                    //_ClearInstancedOwner resyncs on release (audit batch 2)
                    if (_instancedBy == null)
                        meshRenderer.sortingOrder = value;
                }
            }
        }

        public void SetRenderingOrder(UpdateContext context, bool inBatch)
        {
            this.renderingOrder = context.renderingOrder++;

            if (subInstances != null && !inBatch)
            {
                foreach (var sub in subInstances)
                {
                    sub.renderingOrder = context.renderingOrder++;
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="value"></param>
        internal void _SetStencilEraserOrder(int value)
        {
            _stencilEraser.sortingOrder = value;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="value"></param>
        public Color color
        {
            get { return _color; }
            set { _color = value; }
        }

        /// <summary>
        /// 
        /// </summary>
        public void Tint()
        {
            if (_meshDirty)
                return;

            if (_instancedBy != null)
            {
                //color tier (batch 3): skip the native vertex rewrite
                _colorStale = _tintStale = true;
                _contentVersion++;
                _instancedBy._QueueLeafColor(this);
                return;
            }

            int vertCount = mesh.vertexCount;
            if (vertCount == 0)
                return;

            VertexBuffer vb = VertexBuffer.Begin();
            mesh.GetColors(vb.colors);
            List<Color32> colors = vb.colors;
            for (int i = 0; i < vertCount; i++)
            {
                Color32 col = _color;
                col.a = (byte)(_alpha * (hasAlphaBackup ? _alphaBackup[i] : (byte)255));
                colors[i] = col;
            }

            mesh.SetColors(vb.colors);
            vb.End();
            _contentVersion++;
            if (_instancedBy != null)
                _instancedBy._QueueLeafUpdate(this);
            else if (_lastInstancedBy != null)
                _lastInstancedBy._MarkStructureDirty(); //re-admission (e.g. topology became quad again)
        }

        void ChangeAlpha(float value)
        {
            _alpha = value;

            if (_instancedBy != null)
            {
                //color tier (batch 3): skip the native vertex rewrite
                _colorStale = true;
                _contentVersion++;
                _instancedBy._QueueLeafColor(this);
                return;
            }

            int vertCount = mesh.vertexCount;
            if (vertCount == 0)
                return;

            VertexBuffer vb = VertexBuffer.Begin();
            mesh.GetColors(vb.colors);
            List<Color32> colors = vb.colors;
            for (int i = 0; i < vertCount; i++)
            {
                Color32 col = colors[i];
                col.a = (byte)(_alpha * (hasAlphaBackup ? _alphaBackup[i] : (byte)255));
                colors[i] = col;
            }

            mesh.SetColors(vb.colors);
            vb.End();
            _contentVersion++;
            if (_instancedBy != null)
                _instancedBy._QueueLeafUpdate(this);
            else if (_lastInstancedBy != null)
                _lastInstancedBy._MarkStructureDirty(); //re-admission (e.g. topology became quad again)
        }

        /// <summary>
        /// 
        /// </summary>
        public VertexMatrix vertexMatrix
        {
            get { return _vertexMatrix; }
            set
            {
                _vertexMatrix = value;
                _meshDirty = true;

                if (subInstances != null)
                {
                    foreach (var sub in subInstances)
                        sub._vertexMatrix = value;
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public MaterialPropertyBlock materialPropertyBlock
        {
            get
            {
                if (_propertyBlock == null)
                    _propertyBlock = new MaterialPropertyBlock();

                _blockUpdated = true;
                return _propertyBlock;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public void SetMeshDirty()
        {
            _meshDirty = true;

            if (subInstances != null)
            {
                foreach (var g in subInstances)
                    g._meshDirty = true;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public bool UpdateMesh()
        {
            bool ret = false;
            if (_meshDirty)
            {
                UpdateMeshNow();
                ret = true;
            }

            if (subInstances != null)
            {
                foreach (var g in subInstances)
                {
                    if (g.UpdateMesh())
                        ret = true;
                }
            }

            return ret;
        }

        /// <summary>
        /// 
        /// </summary>
        public void Dispose()
        {
            if (_instancedBy != null)
            {
                //leaf-side self-recovery: the stream must not keep rendering a
                //disposed leaf, and the mark must not outlive the leaf
                _instancedBy._MarkStructureDirty();
                _ClearInstancedOwner();
            }
            _lastInstancedBy = null;
            if (mesh != null)
            {
                if (Application.isPlaying)
                    Object.Destroy(mesh);
                else
                    Object.DestroyImmediate(mesh);
                mesh = null;
            }
            if ((_customMatarial & 128) != 0 && _material != null)
                Object.DestroyImmediate(_material);

            if (_texture != null)
            {
                _texture.ReleaseRef();
                _texture = null;
            }

            _manager = null;
            _material = null;
            _matRef = null;
            _matRefOwner = null;
            meshRenderer = null;
            meshFilter = null;
            _stencilEraser = null;
            meshModifier = null;

            if (subInstances != null)
            {
                foreach (var sub in subInstances)
                    sub.Dispose();
                subInstances.Clear();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="context"></param>
        /// <param name="alpha"></param>
        /// <param name="grayed"></param>
        public void Update(UpdateContext context, float alpha, bool grayed)
        {
            Stats.GraphicsCount++;

            if (_meshDirty)
            {
                _alpha = alpha;
                UpdateMeshNow();
            }
            else if (_alpha != alpha)
                ChangeAlpha(alpha);

            //claimed leaves (audit batch 2): the renderer is forceRenderingOff and
            //the stream consumes only the mesh data — skip the whole material
            //pipeline (pooled material lookup, clip uniforms, sortingOrder write
            //happens in SetRenderingOrder). The two blocks above stay: mesh
            //rebuild and alpha change ARE the stream's content push channels.
            if (_instancedBy != null)
                return;

            if (_propertyBlock != null && _blockUpdated)
            {
                meshRenderer.SetPropertyBlock(_propertyBlock);
                _blockUpdated = false;
            }

            if (_customMatarial != 0)
            {
                if ((_customMatarial & 2) != 0 && _material != null)
                    context.ApplyClippingProperties(_material, false);
            }
            else
            {
                if (_manager != null)
                {
                    if (_maskFlag == 1)
                    {
                        _material = _manager.GetMaterial((int)MaterialFlags.AlphaMask | _materialFlags, BlendMode.Normal, context.clipInfo.clipId);
                        context.ApplyAlphaMaskProperties(_material, false);
                    }
                    else
                    {
                        int matFlags = _materialFlags;
                        if (grayed)
                            matFlags |= (int)MaterialFlags.Grayed;

                        if (context.clipped)
                        {
                            if (context.stencilReferenceValue > 0)
                                matFlags |= (int)MaterialFlags.StencilTest;
                            if (context.rectMaskDepth > 0)
                            {
                                if (context.clipInfo.soft)
                                    matFlags |= (int)MaterialFlags.SoftClipped;
                                else
                                    matFlags |= (int)MaterialFlags.Clipped;
                            }

                            _material = GetPooledMaterial(matFlags, blendMode, context.clipInfo.clipId);
                            if (_manager.firstMaterialInFrame)
                                context.ApplyClippingProperties(_material, true);
                        }
                        else
                            _material = GetPooledMaterial(matFlags, blendMode, 0);
                    }
                }
                else
                    _material = null;

                if (!Material.ReferenceEquals(_material, meshRenderer.sharedMaterial))
                    meshRenderer.sharedMaterial = _material;
            }

            if (_maskFlag != 0)
            {
                if (_maskFlag == 1)
                    _maskFlag = 2;
                else
                {
                    if (_stencilEraser != null)
                        _stencilEraser.enabled = false;

                    _maskFlag = 0;
                }
            }

            if (subInstances != null)
            {
                foreach (var sub in subInstances)
                    sub.Update(context, alpha, grayed);
            }
        }

        //Per-frame fast path: when this graphics requests the same material as last frame,
        //skip MaterialManager's dictionary lookup + list scan and only refresh the frame stamp.
        //A cached ref can be re-purposed by MaterialManager once unused for a frame, or its
        //material destroyed, so group/blendMode/material are re-validated before reuse.
        Material GetPooledMaterial(int flags, BlendMode blendMode, uint group)
        {
            MaterialManager.MaterialRef mref = _matRef;
            if (mref != null && _matRefOwner == _manager && _matRefFlags == flags
                && mref.group == group && mref.blendMode == blendMode
                && !_manager.combineTexture && mref.material != null)
            {
                _manager.StampMaterialRef(mref);
                return mref.material;
            }

            mref = _manager.GetMaterialRef(flags, blendMode, group);
            _matRef = mref;
            _matRefOwner = _manager;
            _matRefFlags = flags;
            return mref.material;
        }

        internal void _PreUpdateMask(UpdateContext context, uint maskId)
        {
            //_maskFlag: 0-new mask, 1-active mask, 2-mask complete
            if (_maskFlag == 0)
            {
                if (_stencilEraser == null)
                {
                    _stencilEraser = new StencilEraser(gameObject.transform);
                    _stencilEraser.meshFilter.mesh = mesh;
                }
                else
                    _stencilEraser.enabled = true;
            }

            _maskFlag = 1;

            if (_manager != null)
            {
                //这里使用maskId而不是clipInfo.clipId，是因为遮罩有两个用途，一个是写入遮罩，一个是擦除，两个不能用同一个材质
                Material mat = _manager.GetMaterial((int)MaterialFlags.AlphaMask | _materialFlags, BlendMode.Normal, maskId);
                if (!Material.ReferenceEquals(mat, _stencilEraser.meshRenderer.sharedMaterial))
                    _stencilEraser.meshRenderer.sharedMaterial = mat;

                context.ApplyAlphaMaskProperties(mat, true);
            }
        }

        void UpdateMeshNow()
        {
            _meshDirty = false;
            _colorStale = _tintStale = false; //rebuild bakes colors fresh
            _contentVersion++;
            if (_instancedBy != null)
                _instancedBy._QueueLeafUpdate(this);
            else if (_lastInstancedBy != null)
                _lastInstancedBy._MarkStructureDirty(); //re-admission (e.g. topology became quad again)

            if (_texture == null || _meshFactory == null)
            {
                if (mesh.vertexCount > 0)
                {
                    mesh.Clear();

                    if (meshModifier != null)
                        meshModifier();
                }
                return;
            }

            VertexBuffer vb = VertexBuffer.Begin();
            vb.contentRect = _contentRect;
            vb.uvRect = _texture.uvRect;
            if (_texture != null)
                vb.textureSize = new Vector2(_texture.width, _texture.height);
            else
                vb.textureSize = new Vector2(0, 0);
            if (_flip != FlipType.None)
            {
                if (_flip == FlipType.Horizontal || _flip == FlipType.Both)
                {
                    float tmp = vb.uvRect.xMin;
                    vb.uvRect.xMin = vb.uvRect.xMax;
                    vb.uvRect.xMax = tmp;
                }
                if (_flip == FlipType.Vertical || _flip == FlipType.Both)
                {
                    float tmp = vb.uvRect.yMin;
                    vb.uvRect.yMin = vb.uvRect.yMax;
                    vb.uvRect.yMax = tmp;
                }
            }
            vb.vertexColor = _color;
            _meshFactory.OnPopulateMesh(vb);

            int vertCount = vb.currentVertCount;
            if (vertCount == 0)
            {
                if (mesh.vertexCount > 0)
                {
                    mesh.Clear();

                    if (meshModifier != null)
                        meshModifier();
                }
                vb.End();
                return;
            }

            if (_texture.rotated)
            {
                float xMin = _texture.uvRect.xMin;
                float yMin = _texture.uvRect.yMin;
                float yMax = _texture.uvRect.yMax;
                float tmp;
                for (int i = 0; i < vertCount; i++)
                {
                    Vector2 vec = vb.uvs[i];
                    tmp = vec.y;
                    vec.y = yMin + vec.x - xMin;
                    vec.x = xMin + yMax - tmp;
                    vb.uvs[i] = vec;
                }
            }

            hasAlphaBackup = vb._alphaInVertexColor;
            if (hasAlphaBackup)
            {
                if (_alphaBackup == null)
                    _alphaBackup = new List<byte>();
                else
                    _alphaBackup.Clear();
                for (int i = 0; i < vertCount; i++)
                {
                    Color32 col = vb.colors[i];
                    _alphaBackup.Add(col.a);

                    col.a = (byte)(col.a * _alpha);
                    vb.colors[i] = col;
                }
            }
            else if (_alpha != 1)
            {
                for (int i = 0; i < vertCount; i++)
                {
                    Color32 col = vb.colors[i];
                    col.a = (byte)(col.a * _alpha);
                    vb.colors[i] = col;
                }
            }

            if (_vertexMatrix != null)
            {
                Vector3 camPos = _vertexMatrix.cameraPos;
                Vector3 center = new Vector3(camPos.x, camPos.y, 0);
                center -= _vertexMatrix.matrix.MultiplyPoint(center);
                for (int i = 0; i < vertCount; i++)
                {
                    Vector3 pt = vb.vertices[i];
                    pt = _vertexMatrix.matrix.MultiplyPoint(pt);
                    pt += center;
                    Vector3 vec = pt - camPos;
                    float lambda = -camPos.z / vec.z;
                    pt.x = camPos.x + lambda * vec.x;
                    pt.y = camPos.y + lambda * vec.y;
                    pt.z = 0;

                    vb.vertices[i] = pt;
                }
            }

            mesh.Clear();
            mesh.SetVertices(vb.vertices);
            if (vb._isArbitraryQuad)
                mesh.SetUVs(0, vb.FixUVForArbitraryQuad());
            else
                mesh.SetUVs(0, vb.uvs);
            mesh.SetColors(vb.colors);
            mesh.SetTriangles(vb.triangles, 0);
            if (vb.uvs2.Count == vb.uvs.Count)
                mesh.SetUVs(1, vb.uvs2);
            vb.End();

            if (meshModifier != null)
                meshModifier();
        }

        public void OnPopulateMesh(VertexBuffer vb)
        {
            Rect rect = texture.GetDrawRect(vb.contentRect, flip);

            vb.AddQuad(rect, vb.vertexColor, vb.uvRect);
            vb.AddTriangles();
            vb._isArbitraryQuad = _vertexMatrix != null;
        }

        public NGraphics CreateSubInstance(string name)
        {
            if (subInstances == null)
                subInstances = new List<NGraphics>();

            GameObject newGameObject = new GameObject(name);
            newGameObject.transform.SetParent(gameObject.transform, false);
            newGameObject.layer = gameObject.layer;
            newGameObject.hideFlags = gameObject.hideFlags;

            var newGraphics = new NGraphics(newGameObject);
            newGraphics._vertexMatrix = _vertexMatrix;
            return newGraphics;
        }

        class StencilEraser
        {
            public GameObject gameObject;
            public MeshFilter meshFilter;
            public MeshRenderer meshRenderer;

            public StencilEraser(Transform parent)
            {
                gameObject = new GameObject("StencilEraser");
                gameObject.transform.SetParent(parent, false);

                meshFilter = gameObject.AddComponent<MeshFilter>();
                meshRenderer = gameObject.AddComponent<MeshRenderer>();
                meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                meshRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                meshRenderer.receiveShadows = false;

                gameObject.layer = parent.gameObject.layer;
                gameObject.hideFlags = parent.gameObject.hideFlags;
                meshFilter.hideFlags = parent.gameObject.hideFlags;
                meshRenderer.hideFlags = parent.gameObject.hideFlags;
            }

            public bool enabled
            {
                get { return meshRenderer.enabled; }
                set { meshRenderer.enabled = value; }
            }

            int _sortingOrder;
            public int sortingOrder
            {
                get { return _sortingOrder; }
                set
                {
                    if (_sortingOrder != value)
                    {
                        _sortingOrder = value;
                        meshRenderer.sortingOrder = value;
                    }
                }
            }
        }
    }
}
