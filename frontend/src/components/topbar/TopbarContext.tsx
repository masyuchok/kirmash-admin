'use client';
import { createContext, useContext, useState, ReactNode } from 'react';

export type TopbarButton = {
  icon?: React.ReactNode;
  label: string;
  onClick?: () => void;
  disabled?: boolean;
};

interface TopbarContextType {
  topbarButtons: TopbarButton[];
  setTopbarButtons: (buttons: TopbarButton[]) => void;
}

const TopbarContext = createContext<TopbarContextType | undefined>(undefined);

export const TopbarProvider = ({ children }: { children: ReactNode }) => {
  const [topbarButtons, setTopbarButtons] = useState<TopbarButton[]>([]);
  return (
    <TopbarContext.Provider value={{ topbarButtons, setTopbarButtons }}>
      {children}
    </TopbarContext.Provider>
  );
};

export const useTopbar = (): TopbarContextType => {
  const context = useContext(TopbarContext);
  if (!context) {
    throw new Error('useTopbar must be used within a TopbarProvider');
  }
  return context;
};
