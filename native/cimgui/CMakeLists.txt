# RynthCore cimgui build: upstream cimgui 1.91.6dock (fetched into _src by
# scripts/Build-Cimgui.ps1) plus rynth_imconfig.h and rynth_cimgui_ext.cpp,
# and ImGuiColorTextEdit (fetched into _textedit, patched by
# textedit-rynth.patch) with its C exports in rynth_textedit_ext.cpp.
# Output: a 32-bit cimgui.dll whose structs match ImGui.NET 1.91.6.1.
cmake_minimum_required(VERSION 3.15)
cmake_policy(SET CMP0091 NEW)
project(rynth_cimgui CXX)

set(CMAKE_CXX_STANDARD 17)
# Static CRT, like the stock ImGui.NET native build: no VC++ redistributable needed.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

set(SRC ${CMAKE_CURRENT_SOURCE_DIR}/_src)
set(TEXTEDIT ${CMAKE_CURRENT_SOURCE_DIR}/_textedit)

add_library(cimgui SHARED
    ${SRC}/cimgui.cpp
    ${SRC}/imgui/imgui.cpp
    ${SRC}/imgui/imgui_draw.cpp
    ${SRC}/imgui/imgui_demo.cpp
    ${SRC}/imgui/imgui_tables.cpp
    ${SRC}/imgui/imgui_widgets.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/rynth_cimgui_ext.cpp
    ${TEXTEDIT}/TextEditor.cpp
    ${TEXTEDIT}/LanguageDefinitions.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/rynth_textedit_ext.cpp
)

# Same defines as upstream cimgui's CMakeLists, except the user config.
target_compile_definitions(cimgui PRIVATE
    IMGUI_USER_CONFIG=\"rynth_imconfig.h\"
    IMGUI_DISABLE_OBSOLETE_FUNCTIONS=1
    "IMGUI_IMPL_API=extern \"C\" __declspec(dllexport)"
)
target_include_directories(cimgui PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${SRC}
    ${SRC}/imgui
    ${TEXTEDIT}
    ${CMAKE_CURRENT_SOURCE_DIR}/textedit_shim
)
# ImGuiColorTextEdit uses std::string/printf-style code MSVC warns about.
set_source_files_properties(${TEXTEDIT}/TextEditor.cpp ${TEXTEDIT}/LanguageDefinitions.cpp
    PROPERTIES COMPILE_OPTIONS "/wd4244;/wd4267;/wd4018;/wd4305")
set_target_properties(cimgui PROPERTIES PREFIX "")
target_link_libraries(cimgui imm32)
