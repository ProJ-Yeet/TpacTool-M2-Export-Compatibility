"""
批量 FBX 往返脚本（TpacTool 导出 -> Blender 重导出）。

把 IMPORT_ROOT 下每个 .fbx 用经典(旧)FBX 导入器导入 Blender，再按设定导出到 EXPORT_ROOT
（目录结构镜像保留）。

设置（按需求）：
  - 导入：手动轴向，主骨骼轴向=X，次骨骼轴向=Y
  - 导出：主骨骼轴向=X，次骨骼轴向=Y，添加叶骨=取消，输出帧率=24
  - 每个文件导入前：文件->新建->常规，再清空场景内所有物体，再导入
    （防止不同文件间重名骨骼/物体被 Blender 自动加 .001 后缀）

运行方式：
  1) Blender 内：Scripting 工作区，粘贴/加载本文件，点 Run Script。
  2) 命令行：blender --background --python blender_batch_fbx_roundtrip.py

注意：
  - 导入/导出操作符是经典 `import_scene.fbx` / `export_scene.fbx`。若你的 Blender 导入菜单
    出现两个 FBX 选项（新/旧），经典(旧)操作符名可能不同，请改 FBX_IMPORT_OP。
  - Blender 4.x 可能把“导出动画”总开关从 `use_anim` 改名为 `use_animation`，若报
    `TypeError: keyword 'use_anim'`，把脚本里的 use_anim 改成 use_animation。
"""

import bpy
import os
import sys

# ---------- 可配置项 ----------
IMPORT_ROOT = r"D:\用户文档\Desktop\Bannerlord\animations\human_skeleton"
EXPORT_ROOT = r"D:\用户文档\Desktop\Bannerlord\benlender_export\animations\human_skeleton"

FBX_IMPORT_OP = "import_scene.fbx"   # 经典(旧) FBX 导入器
FBX_EXPORT_OP = "export_scene.fbx"   # FBX 导出器

# 按用户确认的设置：导入/导出都用骨架的"主/次骨骼轴向"，不用"向前/向上"（向前/向上保持默认）。
BONE_PRIMARY_AXIS = 'X'    # 骨架 - 主骨骼轴向
BONE_SECONDARY_AXIS = 'Y'  # 骨架 - 次骨骼轴向
# 导入：不勾"手动方向"，向前/向上跟随 FBX 内嵌方向（Z 轴向上，骨架站立）；骨骼轴向仍用 X/Y。
IMPORT_USE_MANUAL_ORIENTATION = False

# 调试：True = 只导入第一个文件不导出（检查骨架朝向用）；False = 正常完整导入导出。
DEBUG_IMPORT_ONLY = False
OUTPUT_FPS = 24            # 输出帧率

# 只处理这些路径/名称（相对 IMPORT_ROOT）。留空 = 处理全部。
# 匹配规则：相对路径的某个目录名、或文件名(不含 .fbx 扩展名)命中任一项即处理。
TEST_PATHS = []
# ------------------------------


def matches_filters(relpath):
    """TEST_PATHS 为空时全部通过；否则相对路径的目录名/文件名命中任一即通过。"""
    if not TEST_PATHS:
        return True
    rel = relpath.replace('\\', '/')
    parts = rel.split('/')
    stem = os.path.splitext(parts[-1])[0]
    for f in TEST_PATHS:
        f = f.strip().replace('\\', '/').strip('/')
        if not f:
            continue
        if f in parts or f == stem or rel == f or rel == f + '.fbx':
            return True
    return False


def reset_scene():
    """文件->新建->常规，然后彻底清空场景内所有物体，避免导入时重名自动加后缀。"""
    bpy.ops.wm.read_factory_settings(use_empty=True)   # 新建常规空场景
    # 直接移除所有残留物体（比 select_all+delete 更彻底，能清掉隐藏/其它集合里的物体）
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)


def _run_in_3d_view(func, *args, **kwargs):
    """在显式 3D 视图上下文里执行操作符，规避 Blender 5.x 'Context missing active object' 报错。"""
    for window in bpy.context.window_manager.windows:
        for area in window.screen.areas:
            if area.type == 'VIEW_3D':
                with bpy.context.temp_override(window=window, area=area):
                    return func(*args, **kwargs)
    return func(*args, **kwargs)


def import_fbx(filepath):
    _run_in_3d_view(bpy.ops.import_scene.fbx,
        filepath=filepath,
        use_manual_orientation=IMPORT_USE_MANUAL_ORIENTATION,
        primary_bone_axis=BONE_PRIMARY_AXIS,
        secondary_bone_axis=BONE_SECONDARY_AXIS,
        use_anim=True,
    )
    # TpacTool 的动作 FBX 自带一个 "empty_model_node" 占位空物体，导出前清掉，只留骨架。
    for obj in list(bpy.data.objects):
        if obj.name.startswith("empty_model"):
            bpy.data.objects.remove(obj, do_unlink=True)


def export_fbx(filepath):
    # Blender 5.x 导出器没有 use_manual_orientation / use_anim / bake_anim_fps 参数，
    # 输出帧率由场景帧率决定，这里显式设成 24。
    scene = bpy.context.scene
    scene.render.fps = OUTPUT_FPS
    scene.render.fps_base = 1
    _run_in_3d_view(bpy.ops.export_scene.fbx,
        filepath=filepath,
        use_selection=False,              # 导出整个场景（已重置，只有本次导入的内容）
        primary_bone_axis=BONE_PRIMARY_AXIS,
        secondary_bone_axis=BONE_SECONDARY_AXIS,
        add_leaf_bones=False,             # 骨架 - 添加叶骨：取消
        bake_anim=True,                   # 导出烘焙动画
        bake_anim_use_all_bones=True,
    )


def main():
    # 可选命令行参数（用于多进程并行）：blender --background --python 本脚本.py -- 起始序号 结束序号
    # 序号是排序后文件列表的 1 起始索引（含两端）。不带参数 = 处理全部。
    extra = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    range_start = int(extra[0]) if len(extra) > 0 and extra[0].lstrip('-').isdigit() else 1
    range_end = int(extra[1]) if len(extra) > 1 and extra[1].lstrip('-').isdigit() else 10 ** 9

    if not os.path.isdir(IMPORT_ROOT):
        print("导入目录不存在：", IMPORT_ROOT)
        return

    files = []
    for dirpath, _dirs, names in os.walk(IMPORT_ROOT):
        for name in names:
            if name.lower().endswith('.fbx'):
                files.append(os.path.join(dirpath, name))
    files.sort()

    if TEST_PATHS:
        files = [p for p in files if matches_filters(os.path.relpath(p, IMPORT_ROOT))]
        print("TEST_PATHS 生效，匹配到 %d 个文件：" % len(files))
        for p in files:
            print("   ", os.path.relpath(p, IMPORT_ROOT))

    if not files:
        print("导入目录下没有匹配的 .fbx 文件：", IMPORT_ROOT)
        return

    os.makedirs(EXPORT_ROOT, exist_ok=True)

    ok = fail = 0
    total = len(files)
    for i, src in enumerate(files, 1):
        if i < range_start or i > range_end:
            continue
        rel = os.path.relpath(src, IMPORT_ROOT)
        dst = os.path.join(EXPORT_ROOT, rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        print(f"[{i}/{total}] {rel}")
        try:
            reset_scene()
            import_fbx(src)
            if DEBUG_IMPORT_ONLY:
                print(f"    DEBUG_IMPORT_ONLY：已导入 {rel}，请在 3D 视图检查骨架朝向，脚本停止。")
                return  # 停在第一个导入结果上
            export_fbx(dst)
            ok += 1
            print(f"    -> {rel}")
        except Exception as e:
            fail += 1
            print(f"    !! 失败：{type(e).__name__}: {e}")

    print(f"\n完成：成功 {ok}，失败 {fail}。")
    print("输出目录：", EXPORT_ROOT)


if __name__ == "__main__":
    main()
