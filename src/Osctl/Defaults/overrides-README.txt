Put .ini files here to override OpenSim settings for every instance. They are read in alphabetical order and
later files win. Each instance's own overrides (/etc/osctl/instances/<name>/overrides/) are applied after
these. Write only the settings you want to change, under their [Section].
'osctl config set <Section> Key=Value --global' writes 50-osctl.ini here for you.
