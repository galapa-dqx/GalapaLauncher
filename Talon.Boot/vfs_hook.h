#pragma once

// DQX's resource loader override hook. Configuration is supplied before the
// universal unpack barrier fires; resolution and registration happen afterward.
void vfs_set_override_dir(const char* dir);
void vfs_set_census(bool enabled);

// Scan unpacked executable sections, require one VFS signature match, and register it.
// The barrier worker calls hook_install_all() afterward.
bool vfs_resolve_and_register();
