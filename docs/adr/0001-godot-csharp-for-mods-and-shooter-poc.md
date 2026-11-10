# Godot 4 with C# for the base map consumers and the shooter PoC

Mods are built as separate Godot 4 projects in C#, each exporting a client and a headless Linux dedicated server with an authoritative server and Godot's built-in multiplayer (ENet), using Jolt physics. Bevy (Rust), Unity 6 and Unreal 5 were considered: Bevy has no built-in networking and breaks its API often, Unreal needs a source build and C++ for servers, Unity is the fallback. Rust via godot-rust was deferred.

Revisit: once the base map v1 exists, compare Godot against Bevy before committing the second mod. The choice is hard to reverse once mod code exists, which is why the comparison is planned early.
