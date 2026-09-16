cmake_minimum_required(VERSION 3.16)
project(wmeshview CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(OpenGL REQUIRED)
find_package(SDL2 REQUIRED)

add_executable(wmeshview main.cpp)
target_include_directories(wmeshview PRIVATE ${SDL2_INCLUDE_DIRS})

# SDL2main must precede SDL2 on the MinGW link line or SDL_SetMainReady is unresolved.
target_link_libraries(wmeshview PRIVATE
    mingw32
    SDL2main
    SDL2
    SDL2_ttf
    OpenGL::GL
    OpenGL::GLU
)
