# Discovery Manager

**Ape.Module.DiscoveryManager** is an optional Ape module: it advertises this process over SSDP and listens for other Ape instances on the LAN, with a unicast fallback so discovery still works on VPN tunnels where multicast is not forwarded. Optional mDNS browse is off until host JSON names the PTR services to watch.

It is not part of Core. Core stays the scene, replica, and plugin host. This module is a vertical you add when a process needs to find peers.

## Place in the ecosystem

| Piece | Role |
| --- | --- |
| [ape-skeleton](https://github.com/aklen/ape-skeleton) | Workspace vessel (`./ape sync`, `./ape build`) |
| [ape-core](https://github.com/aklen/ape-core) | Framework: scene, DI, plugins, commits |
| [ape-launcher](https://github.com/aklen/ape-launcher) | Host executable |
| **this repo** | SSDP discovery and advertisement, optional mDNS browse |

After `./ape sync` the tree looks like:

```
ape-skeleton/
└── src/
    ├── Ape.Core/
    ├── Ape.Launcher/
    └── Ape.Modules/
        └── Ape.Module.DiscoveryManager/   ← this repository
```

Enable it in `workspace.yaml` under `modules:` (`name: Ape.Module.DiscoveryManager`, this GitHub URL). Host JSON turns the service on. Run from the skeleton:

```bash
./ape run -c src/Ape.Modules/Ape.Module.DiscoveryManager/Samples/discovery-test.json
```

```json
"Ape.Module.DiscoveryManager": {
  "services": {
    "Ape.Module.DiscoveryManager": {}
  },
  "mdns": {
    "browseServices": ["_http._tcp"],
    "scanSeconds": 4,
    "pollSeconds": 12,
    "defaultDescriptorPath": "/device.xml",
    "filter": {
      "locationExcludes": ["127.0.0.1", "0.0.0.0"]
    }
  },
  "ssdp": {
    "vpnUnicastProbe": {
      "enabled": true,
      "rewriteLocationHost": true,
      "probeSubnets": [],
      "extraProbeHosts": [],
      "maxHostsPerSubnet": 254
    }
  }
}
```

The service section loads `DiscoveryManagerService`. SSDP listen and advertisement start with that service. The advertised device is manufacturer `Ape`, friendly name `Ape Instance`, URN `urn:ape:device:core:1`.

mDNS browse stays off when `mdns` is missing, `mdns.enabled` is false, or `browseServices` is empty. The sample lists `_http._tcp`, so that host file turns browse on. Product-specific PTR names belong in the host JSON, not in module defaults.

`ssdp.vpnUnicastProbe` defaults to on. An empty `probeSubnets` list infers subnets from tunnel-like interfaces. `extraProbeHosts` is for peers that a subnet scan cannot reach, such as a `/32`. `rewriteLocationHost` replaces a LAN address in `LOCATION` with the VPN address that actually answered. `maxHostsPerSubnet` caps how many hosts a scan probes.

Discovered locations arrive on `DevicesDiscovered`. A `ssdp:byebye` raises `DeviceLost`. Protocol detail — multicast membership, unicast M-SEARCH, and `LOCATION` rewrite — is in [SSDP-Device-Discovery-and-VPN.md](SSDP-Device-Discovery-and-VPN.md).

Browse names, probe subnets, and which peers to search are configuration. The module does not hard-code a product network.

## License

Copyright (c) 2026 [Akos Hamori](https://github.com/aklen).

Licensed under the [Mozilla Public License 2.0 (MPL-2.0)](https://www.mozilla.org/MPL/2.0/).
