import os
import yaml
from sqlalchemy import create_engine
from sqlalchemy.orm import sessionmaker
from graph import GraphLoader
from hotspots import HotspotScorer

def main():
    with open("test_config.yml", "r") as f:
        config = yaml.safe_load(f)
    
    db_path = config["database"]["path"]
    graphs_dir = config["graphs"]["directory"]
    reports_dir = os.path.join(config["output"]["directory"], "reports")

    engine = create_engine(f"sqlite:///{db_path}")
    Session = sessionmaker(bind=engine)
    session = Session()

    graph_loader = GraphLoader(graphs_dir)
    graph_loader.load_all()

    scorer = HotspotScorer(session, graph_loader, reports_dir)
    scorer.generate_reports()
    print("Hotspot reports generated successfully.")

if __name__ == "__main__":
    main()
