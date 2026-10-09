# IDEA Armory's native parts (docs/agent/EXPLORER.md), for developers who prefer CMake. CI and
# releases use tools/build-native.ps1, which calls the compilers directly with the same flags.
#   cmake -S native -B build/native-x64 -A x64 -DARMORY_VERSION=0.3.3     && cmake --build build/native-x64 --config Release
#   cmake -S native -B build/native-arm64 -A ARM64 -DARMORY_VERSION=0.3.3 && cmake --build build/native-arm64 --config Release --target ArmoryBadges
cmake_minimum_required(VERSION 3.21)
project(ArmoryNative LANGUAGES C CXX RC)

set(ARMORY_VERSION "0.0.0" CACHE STRING "The Version of src/Armory.Agent")
string(REGEX MATCH "^([0-9]+)\\.([0-9]+)\\.([0-9]+)$" _matched "${ARMORY_VERSION}")
if(NOT _matched)
  message(FATAL_ERROR "ARMORY_VERSION must look like 0.3.3, not \"${ARMORY_VERSION}\".")
endif()
set(ARMORY_VERSION_MAJOR ${CMAKE_MATCH_1})
set(ARMORY_VERSION_MINOR ${CMAKE_MATCH_2})
set(ARMORY_VERSION_PATCH ${CMAKE_MATCH_3})
configure_file(ArmoryVersion.h.in "${CMAKE_CURRENT_BINARY_DIR}/ArmoryVersion.h" @ONLY)
include_directories("${CMAKE_CURRENT_BINARY_DIR}" badges shell)

set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")   # /MT: no Visual C++ runtime to install
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
add_compile_definitions(UNICODE _UNICODE)
if(MSVC)
  add_compile_options($<$<COMPILE_LANGUAGE:C,CXX>:/W4> $<$<COMPILE_LANGUAGE:C,CXX>:/GS> $<$<COMPILE_LANGUAGE:C,CXX>:/guard:cf> $<$<COMPILE_LANGUAGE:C,CXX>:/sdl>)
  add_link_options(/guard:cf /DYNAMICBASE /NXCOMPAT /MANIFEST:NO)
  if(CMAKE_GENERATOR_PLATFORM STREQUAL "x64")
    add_link_options(/CETCOMPAT)
  endif()
endif()

# The badge handlers, loaded by explorer.exe (and any process that shows a file dialog).
add_library(ArmoryBadges SHARED badges/ArmoryBadges.cpp badges/ArmoryBadges.def badges/ArmoryBadges.rc)
target_link_libraries(ArmoryBadges PRIVATE advapi32 uuid)
if(MSVC)
  target_compile_options(ArmoryBadges PRIVATE /WX /permissive-)
endif()

# What a right-click item runs: hands one path to the running IdeaArmory.exe.
add_executable(ArmoryShell WIN32 shell/ArmoryShell.c shell/ArmoryShell.rc)
target_link_libraries(ArmoryShell PRIVATE advapi32 shell32 user32 bcrypt)
if(MSVC)
  target_compile_options(ArmoryShell PRIVATE /WX)
endif()

# CI and lab only.
add_executable(BadgeProbe badges/BadgeProbe.cpp)
target_link_libraries(BadgeProbe PRIVATE ole32 advapi32 uuid)
add_executable(ShellPipeTest shell/ShellPipeTest.c)
target_compile_definitions(BadgeProbe PRIVATE _CRT_SECURE_NO_WARNINGS)
target_compile_definitions(ShellPipeTest PRIVATE _CRT_SECURE_NO_WARNINGS)
